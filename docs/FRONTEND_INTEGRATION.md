# HRMS API: development integration guide for the frontend

Status: written 2026-10-23 from the `HRMS` source (uncommitted, not synchronized to `HRMS-Backend`, not deployed). Everything below was read
from the code and covered by the automated tests unless marked **unverified**. JSON is camelCase. Dates are ISO (`2026-10-23`), timestamps UTC.

## 1. Tenant and host requirements

**The company is chosen by the API hostname, and only by it.** `TenantMiddleware` (every path under `/api`) takes `Request.Host.Host`,
`TenantResolver` trims and lowercases it and finds the one active company whose `Companies.Domain` equals it (case-insensitive). Then:

| Situation | Result |
|---|---|
| Host matches no company, an inactive company, or more than one company (ambiguous) | `404 {"message":"Tenant not found or inactive."}` |
| Token's `companyId` claim differs from the host's company | `403 {"message":"Tenant access denied."}` |
| Token without a valid `companyId` | `401` |
| `X-Forwarded-Host`, `X-Company-Id`, a `companyId` in a body or query | **ignored** (proven by `CorsAndHostTests`); a client can never name its tenant |
| Port, scheme, path | not part of the match (`localhost:5000` and `localhost` are both `localhost`) |

`/healthz` and Swagger are outside `/api` and need no tenant.

**Consequence:** one API hostname = one company. There is no "login with a company code" and no shared API URL for several companies. This
contract is unchanged in this task; a shared-host routing mechanism (for example a tenant header or sub-domain scheme) would need its own
security design and tests before it is introduced.

### Simplest safe arrangement for one frontend developer and one test tenant
1. Run the API (or a deployed dev instance) and give the test company's `Companies.Domain` the exact hostname the frontend calls:
   - Local API: domain `localhost` (this is what `DevelopmentDbSeeder` creates; account `admin@localhost`, password from the
     environment variable `HRMS_DEV_ADMIN_PASSWORD`, set by the developer, never committed).
   - Deployed dev API: a dedicated hostname (for example `dev-api.<your-domain>`) whose DNS points at the service, with the same value
     in `HRMS_TENANT_DOMAIN` on first start. The frontend (a different origin) then calls `https://dev-api.<your-domain>`.
2. Do **not** reuse the production company/hostname for development, and do not point a development frontend at the production API.
3. Add the frontend's origin to `Cors:AllowedOrigins` (section 2). In a Vite dev server, call the API URL directly (CORS) or use a Vite
   proxy that forwards the `Host` header of the API; a proxy that rewrites `Host` to `localhost:5173` would make the tenant unresolvable.

## 2. CORS configuration

Key: `Cors:AllowedOrigins` (environment variable form `Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1`, ...; a single comma-separated
value `Cors__AllowedOrigins=https://a.example.com,https://b.example.com` also works).

- Each entry is an origin only: `scheme://host[:port]`. No wildcard, path, query or credentials; anything else stops the application at start.
- **Production:** the key is required, every origin must be `https`, and a missing/invalid value **stops the application at start** (before
  migrations or seeding). **Deployment blocker until set:** the Render service must define `Cors__AllowedOrigins__0` before this build is deployed.
- **Development:** nothing configured = `http://localhost:5173` (Vite default). Configure other origins the same way as above.
- **Other environments (Testing, Staging, ...):** nothing configured = no browser origin is allowed.
- Credentials are never allowed (the API uses an `Authorization` bearer header, not cookies); `AllowAnyOrigin` is never used.
- Exposed response headers: `ETag`, `Retry-After`. Preflight results are cached for 10 minutes.
- Code: `HRMS.API/Extensions/CorsSetup.cs`; tests: `HRMS.Tests/Platform/CorsAndHostTests.cs`.

## 3. Authentication workflow

1. `POST /api/auth/login` `{ "email", "password" }` -> `200 { token, expiresAt, user }`. `user` = `{ id, name, email, role, roleDisplayName,
   permissions, companyId, passwordChangeRequired }`. `role` is the stable role key (`employee`, `manager`, `hr_admin`, `super_admin`);
   `permissions` is `{ "employees": ["view_team", ...], ... }` for hiding UI; **the server re-checks on every request**.
2. Send `Authorization: Bearer <token>` on every later call. The token is valid 8 hours and is bound to a server-side session; it stops
   working at once after `POST /api/auth/logout`, after account deactivation, or when its session is removed.
3. `GET /api/auth/me` -> `{ id, companyId, name, email, role, roleDisplayName, employeeId, permissions, isActive, passwordChangeRequired }`.
   Call it on app start to restore the user and refresh permissions (a role change applies to the next request with the same token).
4. `passwordChangeRequired: true` (first login, temporary password): the only usable endpoints are `/api/auth/me` and
   `POST /api/auth/change-password`; every other protected call answers `403`. Route the user to a change-password screen.
5. `POST /api/auth/change-password` `{ currentPassword, newPassword }` -> `200 {message}`; a wrong current password is `400` with `errors.CurrentPassword`.
6. `POST /api/auth/set-password` (anonymous) `{ token, newPassword }`: completes an emailed invitation link (the token comes from the link).
7. Password rule: at least 12 characters and at most 72 UTF-8 bytes.
8. Store the token in memory (or the most restrictive storage you accept); never log it.

## 4. Endpoint inventory

All except health, login and set-password need the bearer token. "Perm" is the permission the server checks (plus data scope).

| Method and path | Perm / access | Request | Response |
|---|---|---|---|
| `GET /healthz` | anonymous | | `200` |
| `POST /api/auth/login` | anonymous, rate-limited | `LoginCommand` | `LoginResponseDto` |
| `POST /api/auth/logout` | own session | | `200 {message}` |
| `GET /api/auth/me` | own identity (works during forced password change) | | `MeResponseDto` |
| `POST /api/auth/change-password` | own identity (works during forced password change), rate-limited | `ChangePasswordRequest` | `200 {message}` |
| `POST /api/auth/set-password` | anonymous, rate-limited | `SetPasswordRequest` | `200 {message}` |
| `GET /api/employees` | `employees.view_team` | query: `page` (1), `pageSize` (20, max 100), `search`, `status`, `departmentId`, `sortBy` (`empCode`), `sortDirection` (`asc`/`desc`) | `PagedResult<EmployeeDto>` |
| `GET /api/employees/{id}` | `employees.view_own`, scoped (own / reports / company) | | `EmployeeDto` + `ETag` |
| `POST /api/employees` | `employees.create` | `CreateEmployeeRequest` | `201 EmployeeDto` + `Location` + `ETag` |
| `PUT /api/employees/{id}` | `employees.edit_any` | `UpdateEmployeeRequest`, optional `If-Match` | `EmployeeDto` + `ETag` |
| `PUT /api/employees/{id}/deactivate` | `employees.deactivate` | `DeactivateEmployeeRequest`, optional `If-Match` | `DeactivateEmployeeResponse` |
| `POST /api/employees/{id}/invitation` | `employees.create`, rate-limited | | `InvitationIssuedResponse` |
| `GET /api/departments` | `departments.view_own_department` (HR: all; others: own department) | | `DepartmentDto[]` |
| `GET /api/departments/{id}` | same, out of scope = `404` | | `DepartmentDto` |
| `POST /api/departments` | `departments.manage` | `CreateDepartmentRequest` | `201 DepartmentDto` |
| `PUT /api/departments/{id}` | `departments.manage` | `UpdateDepartmentRequest` | `DepartmentDto` |
| `DELETE /api/departments/{id}` | `departments.manage` | | `204`; `409` if it has employees (any status) or child departments |
| `POST /api/departments/{id}/reassign-employees` | `departments.manage` | `ReassignEmployeesRequest` (max 500 ids) | `ReassignEmployeesResponse` |
| `GET /api/roles` | `roles.view` (Super Admin only) | | `RoleDto[]` |

### DTOs (source: `HRMS.Application/Features/*/…Contracts.cs`, `HRMS.API/Contracts/ListEmployeesRequest.cs`; Swagger lists the same shapes)
- `LoginCommand` `{ email, password }`; `ChangePasswordRequest` `{ currentPassword, newPassword }`; `SetPasswordRequest` `{ token, newPassword }`.
- `EmployeeDto` `{ id, empCode, name, email, departmentId, departmentName, managerId?, managerName?, designation, personalEmail?, phone?,
  employmentType?, joiningDate, lastWorkingDate?, status, isActive, role, createdAt }`. `status`: `active|inactive|probation|notice_period|contract`;
  `employmentType`: `full_time|part_time|contract`.
- `CreateEmployeeRequest` `{ name, email, departmentId, managerId?, designation, joiningDate, personalEmail?, phone?, employmentType? }`.
  Company, role, status, employee code are decided by the server; any such field is ignored.
- `UpdateEmployeeRequest` `{ name, departmentId, managerId?, designation, joiningDate, status, lastWorkingDate?, personalEmail?, phone?, employmentType? }` (full replacement of these fields).
- `DeactivateEmployeeRequest` `{ lastWorkingDate }`; `DeactivateEmployeeResponse` `{ employee, sessionsRevoked, directReportsNeedingReassignment }`.
- `InvitationIssuedResponse` `{ expiresAt, deliveryConfigured }` (`deliveryConfigured` is `false` today).
- `PagedResult<T>` `{ items, page, pageSize, totalCount, totalPages }`.
- `DepartmentDto` `{ id, name, parentDepartmentId?, parentDepartmentName?, headEmployeeId?, headEmployeeName?, employeeCount, childDepartmentCount, createdAt }`;
  `CreateDepartmentRequest`/`UpdateDepartmentRequest` `{ name, parentDepartmentId?, headEmployeeId? }`;
  `ReassignEmployeesRequest` `{ toDepartmentId, employeeIds[] }` (source department = route id); `ReassignEmployeesResponse` `{ fromDepartmentId, toDepartmentId, reassignedCount, employeeIds[] }`.
- `RoleDto` `{ id, key, displayName, isSystem, scope (system|company), permissions }`.

## 5. Status codes and error bodies

Error bodies are JSON `{ "message": "..." }`; validation adds `errors`: `{ "field": ["message", ...] }` (400 only).

| Code | Meaning |
|---|---|
| 400 | Validation failed (`errors` map), or an unresolvable request |
| 401 | Missing/invalid/expired token, revoked session, bad credentials. The JWT middleware's own 401 can have an **empty body**: treat any 401 as "sign in again" |
| 403 | Authenticated but not allowed: missing permission, deactivated account, wrong tenant for the token, password change pending. The JWT middleware's 403 can also have an empty body |
| 404 | Unknown tenant host, or an id that does not exist **or is outside your company/scope** (deliberately indistinguishable) |
| 409 | Conflict: duplicate employee/email/department name, delete blocked by employees or child departments, lost concurrent update |
| 412 | `If-Match` did not match the current version |
| 429 | Rate limit; fixed message, `Retry-After` header in seconds |
| 500 | `{ "message": "An unexpected error occurred." }` (no details) |

## 6. Pagination, ETag and If-Match
- Employee list: `page` is 1-based; `pageSize` 1-100 (default 20); invalid values give `400`. Sort fields and directions are validated (`400` on unknown).
- Single-employee responses (`GET`, `POST`, `PUT`) carry `ETag` (a quoted version of the row). Send it back as `If-Match` on `PUT /api/employees/{id}`
  and `PUT /api/employees/{id}/deactivate` to detect lost updates: stale -> `412`, nothing changed. `If-Match` is optional; without it the last write wins but a
  truly concurrent write still ends in `409`. List items do not carry an ETag: re-fetch the single employee before editing. Read `ETag` through CORS (it is exposed).

## 7. Known unavailable functionality
- **No invitation delivery** (no email provider): a newly created employee cannot receive a set-password link. See section 8.
- No role assignment to employees, no role-permission edit endpoints, no company/Super Admin/platform endpoints (`/api/platform/*` does not exist yet).
- No employee self-edit, no "my team" shortcut endpoint (use the list: scope is applied by the server), no Phase 2 modules (attendance, leave, shifts, holidays).
- Reports of a deactivated manager are reported (`directReportsNeedingReassignment`), not reassigned.
- No MFA, no password-reset-by-email ("forgot password") endpoint.

## 8. Test accounts and password setup (no invitation provider)
Recommended, in order of preference. No permanent test credentials are shipped, and none is added by this task.
1. **The seeded administrator.** Local Development: `DevelopmentDbSeeder` creates company `localhost` and `admin@localhost` with the password in the
   environment variable `HRMS_DEV_ADMIN_PASSWORD` (developer-chosen, never committed). Deployed dev instance: `HRMS_TENANT_DOMAIN`,
   `HRMS_ADMIN_EMAIL`, `HRMS_ADMIN_PASSWORD` on first start create the company and an `hr_admin`. With that account the frontend developer can build
   login, `/me`, employee and department screens, and can create further employees.
2. **Other roles (manager/employee) for permission testing:** create employees through the API as the admin; since no email goes out, the owner then
   sets a known test password for that account through a one-off operator step on the **development** database only (hash computed offline with
   BCrypt cost 12; never on production). This is a manual procedure, not an application feature.
3. **Proposal, needs approval before anything is built:** a Development-only `IAccountInvitationSender` that writes the set-password link to a local file
   or the developer's console, enabled only when `ASPNETCORE_ENVIRONMENT=Development` AND an explicit opt-in setting, refusing to start otherwise in Production.
   Rejected as unsafe in any shared environment: it would put a credential in logs. Until approved, option 1/2 apply. The real fix is decision D9 (an email provider).
Invitation tokens and passwords are never logged: the default sender logs only the user id; the outbox stores no token; a reflection test requires
secret-carrying records to hide their values in `ToString`.

## 9. Swagger-based development
- Swagger UI at `/swagger` (and `/swagger/v1/swagger.json`) is enabled in every environment, **including Production** today (non-blocking; consider restricting it before a public launch).
  It lists every endpoint and the DTO schemas; there are no XML descriptions yet.
- Authorize: call `POST /api/auth/login` in Swagger, copy `token`, press **Authorize**, paste the token only (the scheme adds `Bearer`).
- Swagger UI sends the browser's `Host`: opening it at `http://localhost:<port>/swagger` resolves the `localhost` tenant. Opening it through a different
  hostname resolves that company or answers `404 Tenant not found` for `/api` calls.
- Generate a typed client from `/swagger/v1/swagger.json` if you wish; remember the optional `If-Match` header and the `ETag` response header are not modelled as schema fields.

## 10. Client address and rate limits (affects shared environments)
Login (20/min per address, 10 per 5 min per account+address), set-password, change-password and invitation re-issue are rate-limited per instance, in memory.
See `docs/DEPLOYMENT_NOTES.md` for the forwarded-header settings needed behind Render, or all users share the proxy's address.
