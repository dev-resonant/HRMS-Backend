# Deployment notes: configuration, proxy trust and migration release

Status: written 2026-10-23. Inspection and recommendations only; **nothing here was run against Neon or any persistent database**, and
`HRMS-Backend` was not touched. Platform behaviour of Render is described from general knowledge and is marked **unverified** where it matters:
confirm it in the Render dashboard/documentation before relying on it.

## 1. Configuration (names only; never commit values)

| Key (environment variable form) | Needed | Notes |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | always | Secret. |
| `Jwt__Secret` | always | Secret; the app throws at start without it. |
| `Jwt__Issuer`, `Jwt__Audience` | always | Shipped defaults exist in `appsettings.json`. |
| `Jwt__ExpirationMinutes` | optional | 480 in `HRMS`; the older backend copy ships 60: synchronizing changes the lifetime. |
| `Cors__AllowedOrigins__0` ... | **Production: required** | https origins of the frontend; invalid/missing stops start-up. See FRONTEND_INTEGRATION.md section 2. |
| `HRMS_TENANT_DOMAIN`, `HRMS_ADMIN_EMAIL`, `HRMS_ADMIN_PASSWORD` | Production first start | Secret password; remove it after the first sign-in has changed it. |
| `HRMS_SUPERADMIN_*` | optional | Only after the operator designated the platform company (docs/PLATFORM_ADMINISTRATION.md). |
| `ForwardedHeaders__Enabled`, `__KnownProxies__n`, `__KnownNetworks__n`, `__TrustAllProxies`, `__ForwardLimit` | behind a proxy | Section 2. |
| `RateLimiting__*`, `Outbox__*`, `Onboarding__InvitationLifetimeHours` | optional | Defaults are safe. |
| `ASPNETCORE_ENVIRONMENT` | always set explicitly | The Dockerfile does not set it. `Production` runs the production seeder and requires CORS; `Development` runs the development seeder. |

## 2. Client address, rate limiting and trusted proxies

Current behaviour (unchanged by this task; all of it is covered by tests in `RateLimitingTests.cs` and `CorsAndHostTests.cs`):
- Default: **no forwarded header is trusted**. The client address (used for rate-limit keys, `Sessions.IpAddress`, audit and security events) is the TCP peer.
- `ForwardedHeaders:Enabled=true` honours only `X-Forwarded-For` and `X-Forwarded-Proto` (never `X-Forwarded-Host`, so the tenant cannot be influenced) and only
  when the TCP peer is a configured `KnownProxies` address or inside a `KnownNetworks` CIDR range. A peer outside them is ignored even when enabled.
- Enabling it without naming a proxy (and without `TrustAllProxies`), or with a malformed address, **stops the application at start**.
- `ForwardLimit` (default 1) reads that many entries from the right of `X-Forwarded-For`; entries a client prepends are ignored when it matches the real hop count.

### Render (all **unverified** until checked on the real service)
Render terminates TLS in front of the service, so every request's TCP peer is a Render proxy address from a range that is internal and can change.
Do not list a guessed address. Two safe options:
1. **Preferred:** obtain the proxy network from Render (their documentation/support) and set `ForwardedHeaders__Enabled=true` and
   `ForwardedHeaders__KnownNetworks__0=<that CIDR>`.
2. **Acceptable only if the container port is reachable exclusively through Render's proxy** (the normal web-service setup; do not also publish the port
   elsewhere): `ForwardedHeaders__Enabled=true` and `ForwardedHeaders__TrustAllProxies=true`. With a directly reachable port this would let any caller spoof its address.
After enabling either: sign in once and check that the stored `Sessions.IpAddress` (or a `SecurityEvents` row) shows your own public address, not the proxy's and not a
value you can set yourself with a hand-made `X-Forwarded-For`. If more than one proxy (for example a CDN in front of Render) is in the chain, adjust `ForwardLimit`.
Until this is configured every user shares the proxy's address, hence one per-address rate-limit budget (non-blocking for a development release with few users).

## 3. Migrations at start-up: findings and the safe release approach

Findings (code inspection):
- `DevelopmentDbSeeder` and `ProductionDbSeeder` call `Database.MigrateAsync()` on every start. A Render deploy therefore migrates Neon automatically, in the
  application process, with whatever role `ConnectionStrings__DefaultConnection` uses (it must own the tables).
- `HRMS` has 10 migrations, the deployed copy 2. Eight would run on the next start (unverified what Neon holds): `AlignPhase1Schema`, `AddUserEmailNormalizedCheck`, `AddSystemRoles`, `EmployeeConcurrencyToken` (snapshot-only),
  `AddAccountSetupTokensAndEmployeeContactFields`, `AddOutboxAndSecurityEvents`, `RoleConcurrencyToken` (snapshot-only), `AddCompanyIsPlatform`.
- Each migration is transactional in PostgreSQL and the risky ones set `lock_timeout = 15s` and carry guards that abort (counts only) instead of rewriting data.
  If a migration throws during `MigrateAsync`, the application does not start and the failed migration is rolled back (earlier ones in the same run stay applied: each is its own transaction).
- Several instances starting at once: EF takes a migration lock, so one applies and the others wait (a long migration delays the others' start).

**`AddUserEmailNormalizedCheck` with violating data:** its guard raises an exception listing counts only, before the constraint is added, inside the migration's
transaction. Nothing is changed or deleted, the migration is not recorded, the service fails to start, and **the previous release keeps running only if Render
keeps the previous instance** (it normally does when the new instance fails its health check; verify). Fix the offending rows by hand first, then redeploy.

Recommended release procedure (do not skip steps; none was executed here):
1. **Backup:** a restorable snapshot/branch of the Neon database; note its id and the time. Rollback of data is a restore, not a `Down()`.
2. **Preflight (read-only, by the owner):** the five queries in `docs/PROJECT_STATUS.md` ("AddUserEmailNormalizedCheck ... MANDATORY production preflight"): queries 1-3
   must return nothing; query 4 exactly one admin row; also `Employees` must be empty for `AlignPhase1Schema` (user-reported 0), `Roles` with `CompanyId is null` must be empty, and
   the migration role must own the tables. Also confirm `__EFMigrationsHistory` to learn which migrations are really pending.
3. **SQL review:** `dotnet ef migrations script <last-applied> <latest> --idempotent --project HRMS.Infrastructure --startup-project HRMS.API` and read it (never trust the C# alone).
4. **Rehearse on a copy:** restore the backup into a disposable database and run the migrations through the EF runner there (the repository's tests do this with guarded containers).
5. **Apply deliberately, before the application image goes live:** either run the reviewed script (or an EF migration bundle, `dotnet ef migrations bundle`) against Neon from an
   operator machine in a maintenance window, or accept the automatic start-up migration knowing steps 1-4 are done. Keep the automatic behaviour unchanged for now: changing it
   (for example an opt-out flag) needs its own tested change and was not shown to be necessary.
6. **Deploy,** watch the logs for the migration and start-up messages, then check `/healthz` and a login.
7. **Rollback limits:** (a) `Down()` methods exist and were tested on disposable databases, but they drop new columns/tables and so discard data written after the upgrade;
   (b) redeploying the old image against the new schema is **not** guaranteed to work (the old code predates renamed/added columns and constraints, for example the
   email check and role/employee columns), so the only reliable rollback after migration is restoring the backup; (c) take the backup immediately before the release so that
   restoring loses as little as possible.

## 4. Invitation and development-account workflow (recommendation)
- No email provider is bundled (decision D9). `NotConfiguredInvitationSender` only logs the user id; the outbox keeps messages pending (burning no attempts) until a sender
  reports `IsConfigured`; tokens exist only in memory in the outbox processor and are never logged, audited or stored in the outbox.
- For development, use the seeded administrator and, if other roles are needed, a one-off operator step on the development database (FRONTEND_INTEGRATION.md section 8).
- A development-only link writer is **proposed, not implemented**; it needs approval because it must put a credential somewhere a developer can read it.

## 5. Remaining deployment blockers (as of this document)
1. Neon migration state unverified; backup and preflight not done.
2. `Cors__AllowedOrigins__0` must exist on Render before deploying this build, or the service refuses to start.
3. Trusted-proxy settings for Render must be decided and verified.
4. One API hostname per tenant: the frontend developer's host/domain arrangement must be chosen (FRONTEND_INTEGRATION.md section 1).
5. No invitation delivery: employees created through the API cannot set a password.
6. Nothing is synchronized to `HRMS-Backend`, committed, pushed or deployed.
