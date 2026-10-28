# Platform administration: designation, bootstrap, audit and recovery

Status: implemented in `HRMS` on 2026-10-23 (uncommitted, not synchronized, not deployed). **Nothing in this document has been executed against
any persistent database.** The procedures below are for an authorized operator and are written so they can be reviewed before first use.

## 1. The model

- A **platform company** is an ordinary company row with `Companies.IsPlatform = true`. At most one row can have it (unique partial index
  `IX_Companies_IsPlatform_Single`). Every company defaults to `false`. **Nothing designates a company automatically**: not the migration, not
  start-up, not the seeders, not the Super Admin bootstrap. Until an operator designates one, every platform operation is denied.
- A **platform operation** is an action marked `[PlatformOnly]` under the route area `/api/platform/*`. To run it, a caller needs ALL of:
  1. a valid session and token (unchanged authentication);
  2. an active account whose company is the designated platform company - `ICurrentUser.IsPlatform`, read from the database on every request
     (never from the token, a header, a query value, the body or the role name);
  3. every `[RequirePermission]` on the action, from the database-backed role.
  If no company is designated, or more than one is (which the index prevents), `IsPlatform` is false for everybody and the answer is the same
  generic 403 as any other denial.
- Platform authority is **not** data access: employee, department and role endpoints stay bound to the caller's own company. A platform
  administrator sees the platform company's own data there and nothing else.
- Super Admin accounts belong to the platform company. The bootstrap (`SuperAdminProvisioner`) refuses any other company.

## 2. Designating the platform company (manual, authorized operator)

Preconditions: written approval; a restorable backup or snapshot; the target database verified by name, host and port; the company already
exists and is active (create it through the normal, approved company procedure; the application does not create companies for this).

```sql
-- 0. Prove where you are before anything else.
select current_database(), current_user, inet_server_addr(), inet_server_port();

-- 1. Candidates, and what is designated now (normally nothing).
select "Id", "Name", "Domain", "IsActive", "IsPlatform" from "Companies" order by "CreatedAt";

-- 2. Designate exactly one company. Run as one transaction and check the count before COMMIT.
begin;
update "Companies" set "IsPlatform" = true where "Id" = '<PLATFORM_COMPANY_ID>' and "IsActive";
select count(*) from "Companies" where "IsPlatform";            -- must be 1
commit;                                                          -- otherwise: rollback;

-- 3. Leave an immutable trace (SecurityEvents is append-only for the application).
insert into "SecurityEvents"("Id", "CompanyId", "UserId", "EventType", "Reason", "IpAddress", "CreatedAt")
values (gen_random_uuid(), '<PLATFORM_COMPANY_ID>', null, 'platform.company_designated', 'operator_procedure', null, now());
```

Moving the designation: clear the old company and set the new one in the same transaction (the index refuses two at once), then record
`platform.company_designation_moved` the same way. Accounts that were Super Admins of the old company lose platform authority at once (the next
request) and must be re-provisioned in the new platform company.

Rollback of the designation: `update "Companies" set "IsPlatform" = false where "IsPlatform";` - platform operations are then denied for everyone.

Rolling back the schema change itself is the migration `Down` (drops the index and the column); it designates nothing and restores the earlier schema.

## 3. Bootstrapping the first Super Admin

Set `HRMS_SUPERADMIN_EMAIL`, `HRMS_SUPERADMIN_PASSWORD`, `HRMS_SUPERADMIN_TENANT_DOMAIN` (the platform company's domain) and optionally
`HRMS_SUPERADMIN_NAME` in the environment (secrets only through the platform's secret store), then start the application. It creates the account
only if the named company is the designated platform company and no Super Admin exists. The account must change its password at first sign-in;
remove `HRMS_SUPERADMIN_PASSWORD` afterwards. A second platform administrator is not created by the bootstrap: use the platform area once it
offers that, and keep at least two active platform administrators (the specification says 1-2).

## 4. Audit

- Future platform operations use the existing audit infrastructure (`IAuditWriter.Stage`, `IAuditedRequest`, `[AuditExpectation]`). The audit
  row takes the actor's company, so platform actions are recorded **in the platform company's audit log**, visible only there. Where the target is
  another company or a shared system role, its id is part of the audited values.
- Events with no acting user (the bootstrap, and the operator steps in this document) go to `SecurityEvents`.
- Because system roles are shared, a platform change to a built-in role affects every company at the next request. Every such change must be
  audited with the old and new values and guarded by the role invariants and the optimistic-concurrency token.

## 5. Recovering a locked-out platform administrator (manual, never executed here)

There is deliberately **no endpoint, no setting and no automatic reset** for this: a recovery path that the application exposes is an attack
surface. Recovery is an operator procedure with database access.

Preconditions: two people (one requests, one authorizes) and a record of the reason; a restorable backup; the target database verified as in
section 2; confirmation that no other active platform administrator can perform the reset through the platform area.

```sql
-- Identify the account (platform company only).
select u."Id", u."Email", u."IsActive", u."IsFirstLogin", r."Name" as role
from "Users" u join "Roles" r on r."Id" = u."RoleId"
where u."CompanyId" = '<PLATFORM_COMPANY_ID>' and r."Name" = 'super_admin';

-- (a) Account was deactivated:
update "Users" set "IsActive" = true where "Id" = '<USER_ID>' and "CompanyId" = '<PLATFORM_COMPANY_ID>';

-- (b) Password lost. Generate a random temporary password with a CSPRNG and compute its BCrypt hash (work factor 12) OFFLINE with a vetted
--     tool on a trusted machine; never paste the password into the database console or a ticket. Deliver it out of band.
update "Users" set "PasswordHash" = '<BCRYPT_HASH>', "IsFirstLogin" = true
where "Id" = '<USER_ID>' and "CompanyId" = '<PLATFORM_COMPANY_ID>';
delete from "Sessions" where "UserId" = '<USER_ID>';              -- end every existing login of that account

-- (c) The platform company itself was deactivated (all its logins are blocked):
update "Companies" set "IsActive" = true where "Id" = '<PLATFORM_COMPANY_ID>' and "IsPlatform";

-- Trace (append-only for the application; names the account, never a secret):
insert into "SecurityEvents"("Id", "CompanyId", "UserId", "EventType", "Reason", "IpAddress", "CreatedAt")
values (gen_random_uuid(), '<PLATFORM_COMPANY_ID>', '<USER_ID>', 'platform.super_admin_recovered', 'operator_procedure', null, now());
```

Then: the administrator signs in with the temporary password and must change it at once; verify the sign-in and the change in the
application; destroy every copy of the temporary password; review the audit trail and `SecurityEvents` afterwards. If NO Super Admin account
exists at all (and none is deactivated), the bootstrap in section 3 can create one; if a deactivated one exists, reactivate it instead - the bootstrap
deliberately creates nothing while any Super Admin exists.

## 6. Checks an operator can run

```sql
-- Exactly one platform company is expected once designated (0 before).
select count(*) from "Companies" where "IsPlatform";

-- Super Admins outside the platform company (should be none; such accounts have no platform authority but should be removed or moved).
select u."Id", c."Domain" from "Users" u
join "Roles" r on r."Id" = u."RoleId" join "Companies" c on c."Id" = u."CompanyId"
where r."Name" = 'super_admin' and r."CompanyId" is null and not c."IsPlatform";
```

## 7. Production hardening still to plan (not implemented)

- **Multi-factor authentication** for platform accounts (for example TOTP with single-use recovery codes), enforced at login for users of the
  platform company. The specification does not define it; it needs a schema and login-flow decision and must precede real production use of
  the platform area.
- Restricting the platform company's host by network (allow-list or private access) in addition to the existing rate limiting.
- A defined retention and review process for `SecurityEvents` and the platform company's audit log.
