# HRMS Development Log

Chronological record of development sessions with Claude: what was done, commands run, decisions, test results, errors and remaining work. Newest sessions are at the bottom.

Conventions:
- Each session lists operations by type: **Inspection** (read-only), **Modification** (files changed), **Build**, **Test**, **Database** (anything touching a database), **Git/Deploy** (commit, push, sync, deployment).
- "Verified" = observed by Claude. "User-reported" = stated by the user, not observed. "Unverified" = unknown.
- No passwords, connection strings, tokens or secrets appear here. Where a command needed a secret, the log says only that one was required.
- Exact calendar days for some sessions between 2026-10-05 and 2026-10-08 were not recorded in the session; they are marked "(day unverified)".
- Across all sessions: **no commit, no push, no deployment, no Robocopy/synchronization to `HRMS-Backend`, and no connection to Neon was made by Claude.**

---

## Session 1 — 2026-10-05 — Repository inspection and technical assessment

**Inspection only. No files modified.**

Done:
- Listed the repository; read `CLAUDE.md`, all `.cs` sources in API/Application/Domain/Infrastructure, `.csproj` files, `HRMS.slnx`, appsettings files (secret values not printed), both migrations and the model snapshot, and the seeders.
- Extracted the text of `docs/HRMS_Phase1_Phase2_Complete.docx` with PowerShell (`System.IO.Compression` on `word/document.xml`) into the scratchpad; Python was not installed. Read the Phase 1 schema (Tables 1–7), authentication workflow, employee/department workflows, API reference (Section 15) and checklists.
- Read `HRMS-Backend/Dockerfile` and `docker-compose.yml` (read-only; secrets masked). Did **not** open `HRMS-Backend/.env`.

Findings reported (assessment):
- Implemented: tenant resolution from host, login, JWT, session row creation, `/me`, health, Swagger, seeders.
- Not implemented: logout, change-password/first-login, permissions, scope, employees/departments/roles APIs, audit-log writes, email, seeded 4 system roles.
- Session table was written at login but never read; tokens stayed valid until JWT expiry.
- Handlers threw `UnauthorizedAccessException` with no handling middleware (would return 500).
- Schema differed from specification (missing columns/indexes/lengths, text instead of JSONB, cascade on audit FKs).
- Spec inconsistency recorded: Phase 2 header says 6 tables; the detailed schema (Tables 8–16) defines 9.

Errors encountered: none that affected the result (Python missing → used PowerShell).

---

## Session 2 — 2026-10-05 — Authentication hardening

**Decisions from the specification (given by the user):** session/JWT lifetime 8 hours; deactivated user → 403; logout must invalidate the server-side session; every protected request must have an active session.

**Modification** — files created: `ExceptionHandlingMiddleware`, `ForbiddenException`, `ValidationBehavior`, `IClientInfo`, `HttpClientInfo`, `HttpRequestExtensions`, `LoginCommandValidator`, `LogoutCommand`, `LogoutCommandHandler`. Files modified: `loginCommandHandler.cs`, `MeQueryHandler.cs`, `AuthController.cs`, `Program.cs`, `appsettings.json`, `appsettings.Development.json` (expiration 60 → 480 only). Full list in `CHANGELOG.md`.

Decisions and reasons:
- Login verifies the password **before** returning 403 for an inactive account, so account status is not disclosed without credentials.
- A new `ForbiddenException` in Application so the Application layer stays free of HTTP concerns.
- Session check placed in the JWT bearer `OnTokenValidated` event so it applies to every `[Authorize]` endpoint.
- Logout passes the raw token to the command only to hash it; the token is never stored or logged. Logout is idempotent and deletes only the current session.
- Forwarded-headers handling deliberately not added (deployment decision): behind Render the recorded IP will be the proxy's until configured.

**Build:** `dotnet build HRMS.slnx` — succeeded, 0 warnings, 0 errors. No test project exists.

**Test (partial, Claude-observed):** started the API locally (Development, port 5064) and stopped it afterwards. `/healthz` 200; bad login payload 400 with field errors; unknown user 401 (`Invalid email or password.`); malformed JSON 400 (ASP.NET default); logout/`/me` without token 401; with forged token 401. A full login could not be tested because `HRMS_DEV_ADMIN_PASSWORD` was not set in the environment.

Errors encountered:
- A bash heredoc batch used to create files failed to parse; nothing had been written; files were then created with the Write tool.
- Starting the API in Development runs `DevelopmentDbSeeder.SeedAsync` → `Database.MigrateAsync()` against the Development connection target. `AlignPhase1Schema` did not exist yet, so it could not have been applied by these runs; any then-pending original migration would have been applied. The target database's contents were not inspected by Claude at that time (state unverified).

**Migration required:** none.

---

## Session 3 — day unverified, between 2026-10-05 and 2026-10-08 — Live verification attempt (not performed)

**Inspection only.** The user asked for a full live login/me/logout/me sequence using `HRMS_DEV_ADMIN_PASSWORD`. Claude checked whether the variable was set in the process, user and machine scopes (set/unset only, never the value): **unset in all three**. Per instruction, Claude stopped and did not invent credentials. No code or database operation was performed.

---

## Session 4 — day unverified, between 2026-10-05 and 2026-10-08 — Code review of the authentication hardening

**Inspection plus one read-only probe. No files modified.**

- Reviewed `Program.cs` JWT configuration and middleware order, `SessionRepository`, logout, exception middleware, login/me handlers, validation behavior, `HttpRequestExtensions`, `HttpClientInfo`.
- Probe: started the API locally and sent a request with an `Origin` header to check whether error responses from the exception middleware keep CORS headers (the suspicion was that `Response.Clear()` would strip them). Result: CORS header **present** on the 401 from the middleware. The first probe used PowerShell-quoted JSON that was malformed and was discarded; the second used a file body. API stopped afterwards.
- Result: no FAIL items. Potential issues recorded in `PROJECT_STATUS.md` (known issues): lifetime depends on configuration; deactivated-user enforcement only at `/me`; no `TokenHash` index and no expired-session purge; concurrent double logout could return 500; proxy IP; login timing leak; `UserRepository.UpdateAsync` marks `Role` modified; production seeder does not lowercase the admin email; bearer-handler 401/403 have no JSON body; no minimum JWT secret length or pinned algorithm.

---

## Session 5 — day unverified, between 2026-10-05 and 2026-10-08 — Acceptance of authentication hardening

User-reported (not observed by Claude):
- Render has no `Jwt__ExpirationMinutes` environment variable, so 480 minutes from `appsettings.json` applies.
- Manual verification succeeded: login 200 → `/api/auth/me` 200 → logout 200 → `/api/auth/me` with the same token 401.

The user declared the authentication hardening accepted and asked for no further authentication changes at that point.

---

## Session 6 — 2026-10-08 — Phase 1 schema comparison and migration plan

**Inspection only. No files modified.**

- Compared entities, configurations, `DbContext`, snapshot and both migrations with spec Tables 1–7.
- Confirmed gaps: `Users.IsFirstLogin`, `Employees.JoiningDate`, `EmployeeStatus.Contract`, JSONB columns, column lengths, five indexes/unique constraint, audit-log cascade FKs, `Companies.LogoUrl` limit of 50 narrower than the spec.
- Not a schema difference: employee `phone`, `personal email`, `employment type`, `last_working_date` appear only in workflow text, not in Table 4.
- Noted: `employees(manager_id)` index already exists; table names are PascalCase vs the spec's snake_case (not changed); `Employee.Status` is stored as an integer (not changed); token-hash index must be non-unique because the JWT has no `jti`.
- User-reported production preflight (run by the user on Neon, not by Claude): Employees 0, Departments 0, AuditLogs 0, suspect role permissions 0, max session IP length NULL.
- Decisions (user): A — add `LastWorkingDate` (nullable) and `JoiningDate` (required); keep Phone/PersonalEmail/EmploymentType out; B — audit FKs `RESTRICT`; C — enforce 45-char IP limit by normalizing, not truncating; D — `IsFirstLogin` default false, `Contract` enum value, JSONB, lengths/indexes, preserve existing migrations.

---

## Session 7 — 2026-10-12 — Schema-alignment implementation and migration generation

**Modification, build, EF tooling (no database connection).**

Commands (representative):
- `dotnet build` (0 warnings, 0 errors).
- `dotnet ef migrations add AlignPhase1Schema --project HRMS.Infrastructure --startup-project HRMS.API --no-build` — scaffolded; EF warned about possible data loss (type changes).
- `dotnet ef migrations script <from> <to> ...` for Up and Down SQL; `dotnet ef migrations has-pending-model-changes` — "No changes have been made to the model since the last migration."

Defects found by reading the **generated SQL** (not visible in the C# scaffold):
1. `text` → `jsonb` was emitted without `USING` (PostgreSQL would fail). Fixed with explicit `USING col::jsonb`.
2. `JoiningDate` was added with `DEFAULT DATE '-infinity'` that would persist. Fixed: guard + temporary default + `DROP DEFAULT`.

Follow-up corrections requested by the user (same date):
- Made the empty-`Employees` check concurrency-safe: `SET LOCAL lock_timeout = '15s'`, `LOCK TABLE "Employees" IN ACCESS EXCLUSIVE MODE`, then the check, placed at the start of `Up()`.
- `Down()` now uses explicit `USING col::text` for `Roles.Permissions`, `AuditLogs.OldValue`, `AuditLogs.NewValue`.
- Rebuilt; regenerated both scripts; pending-model-changes check clean.

Other errors encountered: `--no-build` used a stale assembly the first time the script was generated (migration "not found") → rebuilt; a PowerShell command with nested quotes failed to parse and did not run.

Risks recorded: lock held to commit on several tables; no test at production volume; `Down()` cannot restore `LogoUrl` to `varchar(50)` if longer data exists.

**Migration was created. It was not applied to any database in this session.**

---

## Session 8 — 2026-10-13 — Resume inspection

**Inspection, build, EF check. No files edited.**

- Confirmed the JoiningDate safeguard (lock, lock timeout, empty-table guard, temporary default dropped) and the explicit JSONB↔text casts are present in the migration; confirmed entities, enum, configurations, snapshot and `HttpClientInfo.cs` match the agreed design.
- `dotnet build` — 0 warnings, 0 errors. `dotnet ef migrations has-pending-model-changes` — no changes.
- Compared `HRMS` against `HRMS BACKUP 03_10_2026` (names only; appsettings diff limited to the expiration line): all differences accounted for by sessions 2 and 7 plus `CLAUDE.md`/`docs/`. Existing migrations identical to the backup.

---

## Session 9 — 2026-10-13 — Disposable-database test of AlignPhase1Schema

### Setup
- Inspected (read-only): environment variables relevant to the target (set/unset only), how the connection string reaches `UseNpgsql`, `docker ps`, and which processes listen on 5432/5433.
- **Findings:** the Development connection string points at `localhost:5432`, served by a **native Windows PostgreSQL**; the Docker container `hrms-postgres` publishes **5433** → 5432. The container has its own `hrms_db` (created by the compose file) plus the user-created `hrms_migration_test`.
- Corrected an earlier statement: `dotnet ef` in this setup does **not** accept `--environment`; the environment must be set with `ASPNETCORE_ENVIRONMENT`.
- The target can be set for EF without source changes via the `ConnectionStrings__DefaultConnection` environment variable. Verified with `dotnet ef dbcontext info` (a placeholder password; no connection opened): database `hrms_migration_test`, data source `tcp://localhost:5433`.
- Role `hrms_migration_tester`: non-superuser, no createdb/createrole/replication/bypassrls. Container `pg_hba`: local socket `trust`, host connections from the host machine require password. The password of this role was set by the user and is **not known to Claude**.
- Therefore **SQL was applied through `psql` inside the container as `hrms_migration_tester`** over the trusted local socket, using the SQL generated by `dotnet ef migrations script`. **The EF Core migration runner (`dotnet ef database update` / `MigrateAsync`) was NOT used in this session.** *(Superseded: the EF runner was tested in Session 11.)*
- Grant made by Claude as `postgres` in `hrms_migration_test` only: `GRANT USAGE, CREATE ON SCHEMA public TO hrms_migration_tester`. (PostgreSQL 16 does not give `CREATE` on `public` by default.)
- The role can still `CONNECT` to `hrms_db` through PostgreSQL's default PUBLIC privilege; this was **not** revoked (it would modify `hrms_db`). Verified the role has no `CREATE` on `hrms_db.public` and no table grants there.
- Guard used before every operation: container name and port 5433 mapping, `current_database() = hrms_migration_test`, `current_user = hrms_migration_tester`, `rolsuper = false`; abort otherwise. Helper scripts were kept in the session scratchpad (not in the repository); they contain no credentials.

### Execution and results (all Claude-observed)
1. Applied `InitialCreate` + `UpdateHRMSModel` (generated script 0 → UpdateHRMSModel). Both recorded in `__EFMigrationsHistory`. Production seeders not run.
2. Inserted synthetic rows: 1 company, 1 role (`Permissions` = `{ }`), 1 user, 1 session, 1 audit-log row with JSON text, 0 employees.
3. Applied `AlignPhase1Schema` (script UpdateHRMSModel → Align): success; history recorded.
4. Schema verification against the catalog: all new/changed columns, types, nullability and defaults as designed (`IsFirstLogin` NOT NULL default false; `JoiningDate` NOT NULL, no default; `LastWorkingDate` nullable; jsonb columns; length limits; `LogoUrl` text); the six new indexes present with `(CompanyId, EmpCode)` unique; the two superseded indexes absent; both audit FKs `RESTRICT`; JSONB data converted (`{ }` → `{}`, objects preserved); existing rows preserved.
5. Rollback (script Align → UpdateHRMSModel): success; schema fingerprint identical to the pre-migration baseline (101 lines); history record removed; data preserved as text.
6. Guard test: inserted a department and an employee (`Status = 5`) at the baseline schema, then applied AlignPhase1Schema → aborted with the intended exception; schema unchanged versus baseline; history unchanged (atomic).
7. Concurrency test: a second session held an uncommitted insert into `Employees`; the migration waited, then failed with "canceling statement due to lock timeout" (about 16 s including the target check); schema unchanged; the holder rolled back.
8. Reapplied AlignPhase1Schema with `Employees` empty: success; schema fingerprint identical to the first application (107 lines).
9. Behavior checks (each in a rolled-back transaction): insert without `JoiningDate` rejected (NOT NULL); insert with `JoiningDate` and `Status = 5` accepted, `LastWorkingDate` NULL; duplicate `EmpCode` in the same company rejected; same `EmpCode` in a different company accepted; 21-character `EmpCode` rejected; 46-character session IP rejected; invalid JSON in `Roles.Permissions` rejected; deleting a user that has audit rows blocked (`RESTRICT`).

### Errors encountered (test-harness, not migration defects)
- `--environment` option rejected by `dotnet ef` → switched to `ASPNETCORE_ENVIRONMENT`.
- Several first-pass behavior checks used mis-escaped SQL (PowerShell single-quoted strings do not unescape doubled quotes). Two of them (duplicate `EmpCode`, over-length `EmpCode`) appeared to "pass" for the wrong reason; those results were discarded and the checks rerun correctly.
- A history query had a quoting slip; rerun.
- `pg_dump` hash comparison was invalid (PostgreSQL 16 embeds a random token in plain-text dumps, so two dumps of an unchanged database differ). It had falsely reported `hrms_db` as changed; replaced by the checks below.
- Tool safety filter blocked a container-side `rm` cleanup; test SQL files remain in the container's `/tmp` (harmless, contain only test SQL).

### State of `hrms_db` (Docker container's copy) after the tests
Checked read-only: migration history still the original two migrations; 8 public tables; schema fingerprint identical to the expected UpdateHRMSModel schema (100 lines); zero inserts/updates/deletes recorded in table statistics; no open connections; normalized dump hash is stable (recorded for future comparison). **It was not intentionally modified.** The native-Windows `hrms_db` on port 5432 was **not accessed** in this session and its current state is unverified.

### Not tested
*(At the end of this session; the EF Core migration runner was tested later — Session 11.)* EF Core migration runner against AlignPhase1Schema; `HttpClientInfo` IP normalization; behavior at production data volume; Neon-specific considerations (for example table ownership of the production role); startup behavior of the application when a pending migration is applied by `MigrateAsync`.

State left behind: disposable database `hrms_migration_test` is at AlignPhase1Schema with synthetic rows (1 company, 1 role, 1 user, 1 session, 1 audit-log row, 1 department, 0 employees).

---

## Session 10 — 2026-10-14 — Documentation

**Modification (documentation only).** Updated `CLAUDE.md`; created `docs/CHANGELOG.md`, `docs/DEVELOPMENT_LOG.md`, `docs/PROJECT_STATUS.md`. Inspected the existing `CLAUDE.md` (dated 2026-10-03) and `docs/` (contained only the specification `.docx`) first; all previous `CLAUDE.md` content preserved, with status notes added. Read-only checks: line endings of `CLAUDE.md`, presence of `HRMS-Backend/.github/workflows`, that `HRMS-Backend` is a Git work tree. No application code, migration, database, Git or deployment operation.

---

## Session 11 — 2026-10-14 — EF Core migration-runner test of AlignPhase1Schema

**Database operations on the disposable database `hrms_migration_test` only. No source, migration or documentation file was edited during the test itself.**

### Credential setup (no secrets recorded)
- Attempted EF authentication as `hrms_migration_tester` with no password configured: `dotnet ef migrations list` failed with "No password has been provided but the backend requires one (in SASL/SCRAM-SHA-256)". Claude stopped; it did not use the `postgres` superuser, did not reset the role's password and did not switch connection route.
- Checked (read-only, counts/existence only) `%APPDATA%\postgresql\pgpass.conf`: neither the `postgresql` directory nor the file existed; `PGPASSFILE` and `PGPASSWORD` were unset.
- Provided a revised setup/cleanup plan: append-only setup with run-time re-checks (non-ASCII content, existing marker or tester entry, wildcard entries that could shadow ours), a marker comment line plus the exact tester entry, non-secret state flags in `%TEMP%`, and a cleanup that removes exactly that two-line block and verifies the original byte length before writing (or deletes the file/directory only if the setup created them). The password was never requested or displayed in chat.
- The user created the temporary entry (USER-REPORTED; its contents were never displayed to Claude).

### Authentication probe (read-only)
`dotnet ef migrations list` with `ASPNETCORE_ENVIRONMENT` and `DOTNET_ENVIRONMENT` = `Production` (so `appsettings.Development.json` cannot redirect to port 5432) and a connection string with host `localhost`, port `5433`, database `hrms_migration_test`, role `hrms_migration_tester`, no password. Exit code 0; only `SELECT ... FROM "__EFMigrationsHistory"` executed; all three migrations listed with no `(Pending)` marker.

### Rollback and reapply through the EF runner
Each database-changing command was preceded by two guards: a client-side check of the exact connection values (host, port, database, role, environment, no password in the string) and a server-side check over the container (container name and port mapping, `current_database()`, `current_user`, `rolsuper = false`).
1. Pre-state: history = `InitialCreate`, `UpdateHRMSModel`, `AlignPhase1Schema`; schema identical to the previously verified aligned schema (106 lines); synthetic-data content hashes recorded.
2. `dotnet ef database update 20260831122714_UpdateHRMSModel --project HRMS.Infrastructure --startup-project HRMS.API` → exit 0, "Reverting migration '20261012114231_AlignPhase1Schema'". History = `InitialCreate`, `UpdateHRMSModel`; schema identical to the pre-`AlignPhase1Schema` baseline (100 lines); synthetic data identical by count and content hash (1 company, 1 role, 1 user, 1 session, 1 audit-log row, 1 department, 0 employees).
3. `dotnet ef database update AlignPhase1Schema --project HRMS.Infrastructure --startup-project HRMS.API` → exit 0, "Applying migration '20261012114231_AlignPhase1Schema'". (The `RAISE EXCEPTION` text in the console output is the migration's own SQL echoed by EF, not an error; `Employees` was empty.)
4. Final verification (queries run as `hrms_migration_tester` over the container socket): all three migrations recorded; schema identical to the previously verified aligned schema (106 lines); `JoiningDate` date NOT NULL with no default; `LastWorkingDate` nullable; `IsFirstLogin` boolean NOT NULL default false; `Roles.Permissions`, `AuditLogs.OldValue`, `AuditLogs.NewValue` are `jsonb` with JSON objects; five new indexes present with `(CompanyId, EmpCode)` unique; superseded indexes absent; both audit-log FKs `RESTRICT`; synthetic data identical to before the rollback; `dotnet ef migrations has-pending-model-changes` → no changes.

### Errors encountered
- First attempt of the sequence stopped itself before the rollback: the pre-state schema comparison reported a difference. Cause: a bug in the test harness (a multi-line string compared unsplit against a list of lines). A line-by-line comparison showed the schema identical, the helper was fixed, and the whole sequence was rerun from the start. No database change occurred in the aborted attempt.
- No error in the rollback or the reapplication.

### Not done / not tested
Production-scale locking duration; Neon migration-role ownership; application-startup `MigrateAsync` (the application and its seeders were not run); `HttpClientInfo` IP normalization; guard-abort and lock-timeout behavior through the EF runner (SQL level only, Session 9). `hrms_db` (container and native), Neon, `HRMS-Backend`, Git and deployment were not accessed or modified.

State left behind: `hrms_migration_test` at `AlignPhase1Schema` with the same synthetic rows.

### Credential cleanup (confirmed later on 2026-10-14)
The user ran the agreed cleanup script. Claude then verified, without displaying or reading any file contents: `%APPDATA%\postgresql\pgpass.conf` does not exist, the `postgresql` directory does not exist, the temporary state file in `%TEMP%` does not exist, and `PGPASSFILE`, `PGPASSWORD` and `ConnectionStrings__DefaultConnection` are unset. Because the file is absent, the temporary marker and tester entry cannot remain. No database was connected to and no file other than these two documentation files was modified for this confirmation.

---

## Session 12 — 2026-10-14 — Phase 1 audit, then authentication test harness and the D1 fix

### Phase 1 audit (read-only; no changes)
A comprehensive read-only audit (source, documents and migration code only; no builds, databases or migrations) produced the requirements matrix, 14 defects, risks and a roadmap. Highest items: no authorization or scope layer; tenant isolation by convention only; **D1** (login overwrote concurrent changes); deactivation not enforced per request; no automated tests. The user then approved the first task: an authentication test harness and the D1 fix.

### Plan and approvals
Plan proposed and approved before any change: Testcontainers disposable PostgreSQL 16; xUnit v2; plain xUnit assertions; no unrelated packages; D1 regression test written first and run against the unmodified code; `UpdateLastLoginAsync` and removal of `UpdateAsync` if unused; bcrypt cost test added as skipped (cost not changed); documentation updated afterwards. NuGet restore and Docker Hub access approved.

### What was done (in order)
1. **Inspection (read-only):** SDKs (10.0.401), NuGet sources, absence of cached test packages, local Docker images (`postgres:16` present), `Program` visibility, `UpdateAsync` usage.
2. **Package versions** looked up on nuget.org; project `HRMS.Tests` created by hand (no template) and added to `HRMS.slnx`.
3. **Test infrastructure and tests written:** fixture and isolation guard, web host factory, data/JWT/HTTP helpers, `LoginDataIntegrityTests` (D1) first, then `LoginTests`, `SessionValidationTests`, `LogoutTests`.
4. **Build problems fixed (test project only):** `app.Use` overload needed `using Microsoft.AspNetCore.Builder`; an `MSB3277` warning (EF Core Relational 10.0.4 from Npgsql vs 10.0.9 in `HRMS.Infrastructure`) fixed by a direct `Microsoft.EntityFrameworkCore.Relational` 10.0.9 reference in the test project.
5. **First test run failed for an environmental reason:** `Mvc.Testing` computes the API content root with an MSBuild property function that breaks on the apostrophe in `Aditya's_Backup` (`DirectoryNotFoundException` with an unevaluated `$([System.IO.Path]::GetDirectoryName(...))`). Fixed by setting the content root explicitly in the factory. The disposable container itself started, migrated and was removed correctly.
6. **RED run (unmodified production code):** D1 test **failed** — `Assert.False() Failure, Expected: False, Actual: True` on `IsActive` (the login's update reverted the concurrent deactivation); the second D1 test (same-values check) passed. Full suite at that point: 37 tests, 36 passed, **1 failed (only D1)**.
7. **Fix:** `IUserRepository.UpdateAsync` removed, `UpdateLastLoginAsync(userId, companyId, utcNow)` added (company-scoped single-column `ExecuteUpdate`), login handler changed. `UpdateAsync` had one call site.
8. **GREEN run:** solution build 0 warnings / 0 errors; 37 tests, 37 passed (both D1 tests pass).
9. **Remaining approved tests added:** `UserRepositoryTests`, `TokenHasherTests`, `HttpClientInfoTests`, `LoginCommandValidatorTests`, `ExceptionHandlingMiddlewareTests`, `JwtTokenServiceTests`, `PasswordHasherTests` (with the skipped bcrypt-cost test), `MigrationsOnEmptyDatabaseTests`, `SeederTests` (disposable database only; includes a skipped test for defect D3).
10. **First full run: 82 tests, 78 passed, 2 failed, 2 skipped.** Both failures were mistakes in the new tests, not production defects: a SQL cast (`confdeltype` is a PostgreSQL `"char"`), and an off-by-one in an email-length test (250 + `@x.io` = 255, not 256). Fixed.
11. **`TestDatabaseGuardTests` added** (12 tests) to prove the guard rejects the native server's port, the compose container's port, a remote host, `hrms_db`, `hrms_migration_test` and a foreign marker.
12. **Final run: 94 tests — 92 passed, 0 failed, 2 skipped.** Solution build: 0 warnings, 0 errors. Wall time about 31 s.

### Safety checks
- Every run created its own container and removed it; before and after the runs `docker ps -a` showed only the three pre-existing containers (`hrms-api`, `hrms-postgres`, `hrms-api-container`), with `hrms-postgres` never restarted.
- The guard and the container's random port (never 5432/5433) prevented any connection to `hrms_db`, `hrms_migration_test`, the native server or Neon. No `postgres` superuser was used against any existing database; no existing database was queried in this task.
- No migration was run against an existing database; no schema, migration, credential, JWT secret, Docker or `HRMS-Backend` change; no Robocopy, commit, push or deploy.

### Outstanding after this session
- Skipped tests (not passing): bcrypt cost 12+ (spec Table 3, unchanged by design) and D3 mixed-case seeded admin email.
- Untested: a full `Program.Main` start in Development/Production; guard-abort and lock-timeout through the EF runner; production-scale locking; Neon role ownership; behavior behind a real proxy.
- Phase 1 features still not implemented (authorization/scope, employees, departments, roles, audit writing, change-password/first-login).
- All changes are uncommitted and unsynchronized with `HRMS-Backend`.

---

## Session 13 — 2026-10-14 — Focused review of the test harness, then the BCrypt work-factor correction

### Review (read-only; no repository changes)
Reviewed the D1 fix, the Testcontainers fixture and `TestDatabaseGuard`, the skipped tests and the email and BCrypt behavior. Findings recorded to the user: no seeder test had failed — the one that "did not pass" was the intentionally **skipped** mixed-case seeded-email test (defect D3). D3 was **reproduced** against a disposable container with a throwaway project kept in the session scratch folder (not in the repository), reusing the fixture and guard and the real handler, repositories and seeder: a seeded `Admin@…` email is stored as given and cannot log in as the configured case, lowercase or uppercase; two users differing only by case can coexist (case-sensitive unique index). Cause: production code plus an unresolved case policy in the specification (the test is correct). Also measured: the production bcrypt cost was 11; bytes beyond 72 are ignored (a 72-character password plus `X` verifies against one ending in `Y`) while the login validator allows 128 characters; hashing took about 271 ms at cost 11 and 575 ms at cost 12. Proposed (not implemented): lowercase email normalization via one shared normalizer, then a `CHECK ("Email" = lower("Email"))` migration after a preflight.

### Change (approved scope: BCrypt work factor only)
1. `BCryptPasswordHasher`: `WorkFactor = 12` passed explicitly to `HashPassword`; `Verify` untouched (older hashes verify by reading their own cost).
2. Tests: skip removed from `Production_hashing_cost_is_at_least_12`; added an exact-12 test, a cost-11-hash-still-verifies unit test, and two login-endpoint tests (a cost-11 hash logs in; newly created users have `$2a$12$` hashes and log in).
3. **Build:** solution 0 warnings, 0 errors. **Tests:** 98 total, 97 passed, 0 failed, 1 skipped (D3). The first build/run passed without any fix-up. Run time about 50 s (was about 29 s) because hashing is slower.
4. Isolation: the run used only the established disposable Testcontainers database; `docker ps -a` before and after showed only the three pre-existing containers, unchanged. No existing database, Neon, migration, `HRMS-Backend` or credential was touched; no Robocopy, commit, push or deploy.

### Not done (by instruction)
Email normalization and the D3 test; rehash-on-login; the 72-byte password limit; login rate limiting.

---

## Session 14 — 2026-10-14 — Email normalization (defect D3) and the `AddUserEmailNormalizedCheck` migration

### Inspection (before any change)
Production email write paths: only the two seeders (`ProductionDbSeeder` stored `HRMS_ADMIN_EMAIL` as given; `DevelopmentDbSeeder` used the constant `admin@localhost`). Login only reads (and already trimmed/lowercased its input). No controller, handler or repository creates or updates users; no raw SQL on `Users`. Constraints before: unique `(CompanyId, Email)` (case-sensitive), `Email` varchar(255). Existing tests: the skipped D3 test and a few login tests.

### Decisions
- One rule: `Trim()` + `ToLowerInvariant()`, in a shared `EmailNormalizer` (Domain), applied by login, both seeders and the `User.Email` setter (so future paths cannot bypass it).
- A migration is warranted: only a database constraint stops raw SQL or future code from storing case-variant duplicates. `CHECK ("Email" = lower(btrim("Email")))` plus the existing unique index gives case-insensitive per-company uniqueness. The migration must **not** rewrite data: it aborts (counts-only message, transaction rolled back) if any existing row would violate the rule or two users would collide.
- Explained to the user before creating the migration (schema change and reason, as required).
- *(Superseded the same day, Session 15: the first constraint used the one-argument `btrim("Email")`, which trims only the plain space; it now passes the full `string.Trim()` character set.)*

### Work and commands
1. Added `EmailNormalizer`; changed `User.Email`, login handler, both seeders; added the check constraint to `UserConfiguration`. Build 0 warnings / 0 errors. `has-pending-model-changes` → "Changes have been made" (expected).
2. `dotnet ef migrations add AddUserEmailNormalizedCheck` (`ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` = `Production`, no database connection). Scaffold contained only the check constraint; added `SET LOCAL lock_timeout`, the abort-on-violation guard. Generated Up and Down SQL read: Up = lock timeout, guard, `ADD CONSTRAINT`, history insert; Down = `DROP CONSTRAINT`, history delete. `has-pending-model-changes` → none.
3. Tests: new `EmailNormalizerTests`, `UserEmailConstraintTests`, `EmailNormalizationMigrationTests` (uses a new guarded scratch database inside the disposable container so the migration is exercised against existing, non-empty data); D3 test enabled; seeder/login tests extended.
4. **First full run: 127 tests, 126 passed, 1 failed** — `MigrationsOnEmptyDatabaseTests.All_three_migrations_…` hard-coded three migrations. Updated to four (not weakened) and added the constraint to its schema check.
5. **Final run: 127 tests — 127 passed, 0 failed, 0 skipped**, about 43 s. Solution build 0 warnings / 0 errors. `has-pending-model-changes`: none.

### Safety
- Only the established guarded Testcontainers environment was used (`docker ps -a` before/after: the three pre-existing containers unchanged; the throwaway container and its helper removed). No existing database, Neon, `hrms_db`, `hrms_migration_test` or the native server was contacted. The migration was not applied to any persistent database. No `HRMS-Backend` edit, Robocopy, commit, push or deploy.
- BCrypt (`WorkFactor = 12`) was not modified; its tests still pass (cost-11 hashes verify, new hashes cost 12).

### Remaining risks and required production preflight
- Run the five read-only email queries in `PROJECT_STATUS.md` §6 on production before deploying (all must return nothing). Because migrations run at application start, a violating row would stop the deployment from starting. Take a backup/branch first.
- `lower()` (PostgreSQL) and `ToLowerInvariant()` (.NET) can disagree for exotic non-ASCII characters; query 4 checks for such emails. Tab/newline padding is trimmed by the application but not by `btrim`; query 3 checks for it.
- `Companies.Domain` has the same as-given/case-sensitive pattern (not changed).
- Still untested: production-scale locking, Neon role ownership, a full `Program.Main` start, behavior behind a real proxy.

---

## Session 15 — 2026-10-14 — Read-only review of the email migration, then the whitespace-policy correction

### Review (read-only; no files changed)
Compared `EmailNormalizer` (`Trim()` + `ToLowerInvariant()`) with the constraint `"Email" = lower(btrim("Email"))`. A pure .NET probe (scratch project, no database) listed the 25 BMP characters `Trim()` removes and confirmed it strips all of them at the edges; PostgreSQL's one-argument `btrim` removes only U+0020 (documented behavior, not executed in that review). Finding: the database was more permissive than the application for 24 whitespace characters, so raw SQL could store an unreachable or duplicate account, and the migration guard (same expression) would not catch such legacy rows. Also reviewed: migration order and transactionality (guard before constraint, rollback on `RAISE EXCEPTION`, `Down()` removes only the constraint), test validity (migration tests do run the real EF runner; whitespace was tested only with plain spaces at database level), and the preflight queries (query 3 imprecise/locale-dependent, query 5 was prose). Verdict then: deployment blocked until corrected.

### Correction (approved scope: whitespace only)
1. Chose the policy: the database trims exactly the characters `string.Trim()` trims — the 25 BMP `char.IsWhiteSpace` characters — at both ends; everything else, including U+200B/U+FEFF and inner whitespace, is left alone by both sides.
2. Added `UserEmailPolicy` (single list + generated SQL), switched `UserConfiguration` to it, and edited the migration, its designer and the snapshot **in place** with the identical literal (the migration had never been applied to a persistent database). Guard and constraint use the same list. Build 0 warnings / 0 errors; `has-pending-model-changes`: none; generated Up and Down SQL read.
3. Tests: all-25-characters × both ends at database level; migration against existing rows containing each kind of invalid whitespace (real EF runner, throwaway databases, rows compared byte-for-byte before/after); accepted cases; an exhaustive BMP probe of the applied constraint; unit tests tying the policy list to `Trim()` itself.
4. **Run: 226 tests, 226 passed, 0 failed, 0 skipped** (about 60 s). No test failed on the first full run after the change. The new whitespace tests were not executed against the old constraint (no red run for this change); by construction they would fail because the old expression trims only U+0020.
5. The exhaustive test also reported a **case-mapping disagreement**: this PostgreSQL 16 build lowercases U+0130, U+1C89, U+A7CB, U+A7CC, U+A7DA, U+A7DC, which `ToLowerInvariant()` does not (the reverse direction has no instances). Recorded as a remaining risk, deliberately not changed (outside the requested scope).

### Safety
Only the guarded Testcontainers environment (and a scratch database inside it) was used; `docker ps -a` before/after showed the three pre-existing containers unchanged. No existing database, Neon, `hrms_db`, `hrms_migration_test` or the native server was contacted; the migration was not applied to any persistent database; BCrypt untouched; `Companies.Domain` untouched; no `HRMS-Backend` edit, Robocopy, commit, push or deploy.

### Remaining deployment blockers
Production preflight (`PROJECT_STATUS.md` §6, queries 1–3 empty; check encoding, locale and table ownership); decision on the six case-mapping characters; `AlignPhase1Schema` items (production-scale locking, Neon ownership, `Employees` empty); nothing committed, synchronized or deployed.

---

## Session 16 — 2026-10-14 — Read-only review of company-domain handling, then the minimal correction

### Review (read-only)
Read `ProductionDbSeeder`, `DevelopmentDbSeeder`, `Company` and its configuration, `TenantResolver`/`TenantContext` and the tests. Generated the SQL both lookups produce without any database connection (a scratch program that only asks EF for query text): seeder `WHERE "Domain" = @tenantDomain`, resolver `WHERE lower("Domain") = @host LIMIT 1` with no `ORDER BY`. Findings: no normalization on write, seeder exact-match, unique index case-sensitive with no collation or check constraint, resolver picks an arbitrary row, no existing test for a mixed-case or padded domain. Failure scenarios derived from the code: a changed casing of `HRMS_TENANT_DOMAIN` creates a second company/role/admin, and with two companies differing only by case the resolver can issue a token for the wrong tenant; whitespace or a port in the setting creates an unreachable stray company.

### Correction (approved scope; no migration)
1. `DomainNormalizer` (Domain layer); `Company.Domain` normalizing setter; both seeders; `TenantResolver` (normalized host, `Take(2)`, fail closed on ambiguity with a safe Error log naming the host and company ids). Production seeder: reuses a single case-insensitive match unchanged, creates a company only when none exist, otherwise throws and creates nothing (also when two match). Build 0 warnings / 0 errors; `has-pending-model-changes`: none (no schema change).
2. **Existing suite against the new seeder:** 6 of 226 tests failed with the intended message ("does not match any existing company, and companies already exist") — every one bootstrapped a new company in the shared database that other tests had populated. They were not weakened: each now runs the same assertions in a fresh, empty scratch database inside the disposable container (the true precondition of first-tenant bootstrap).
3. Test infrastructure: `HrmsWebApplicationFactory` can host the API against a guarded scratch database; `ScratchDatabase.MigrateToLatestAsync`; `CapturingLoggerProvider` for the diagnostic assertion.
4. New `CompanyDomainTests` (17) and `DomainNormalizerTests` (9).
5. **Red run:** to prove the tests detect the defect, the four production files were backed up, temporarily put back to the old behavior, and the 36 focused tests run: **14 failed** (duplicate companies on any casing/padding, no refusal, entity not normalizing, and the ambiguity test — the old resolver returned 200 (a successful login) instead of 404). The files were then restored from the backups and verified byte-identical by SHA-256.
6. **Pitfall found and fixed:** the first complete run after restoring still showed 14 failures with the old behavior. Cause: `Copy-Item` preserved the backups' older timestamps, so the incremental build treated the outputs as up to date and did not recompile; the compiled test binaries were still the red-run ones. A forced rebuild (`--no-incremental`, after touching the four files without changing their contents) resolved it. Any result reported earlier in that run was discarded; the final results come from the rebuilt binaries.
7. **Final run: 252 tests — 252 passed, 0 failed, 0 skipped** (about 90 s). Email-normalization and BCrypt tests unchanged and passing; BCrypt `WorkFactor = 12` untouched.

### Safety
Only the guarded Testcontainers environment (and scratch databases inside it); `docker ps -a` before/after: the three pre-existing containers unchanged. No existing database, Neon, `hrms_db`, `hrms_migration_test` or the native server contacted; no migration created or applied; JWT, sessions, BCrypt, email normalization, permissions untouched; no `HRMS-Backend` edit, Robocopy, commit, push or deploy.

### Migration decision and remaining risks
No migration is needed for the fix itself. Follow-up recommended and documented, not created: `CK_Companies_Domain_Normalized` (lowercase DNS-host allowlist) with the guard pattern of the email migration, plus production preflight (`PROJECT_STATUS.md` §6). Remaining: the database still accepts case/whitespace-variant domains until that constraint exists (the resolver now fails closed instead of guessing); the seeder's new refusal is a startup failure mode if `HRMS_TENANT_DOMAIN` stops naming the existing company, so the preflight query must confirm exactly one match; legacy stray companies, if any, are untouched.

---

## Session 17 — 2026-10-16 — Authorization foundation

### Task and approved decisions
Implement the authorization foundation only. D1: Employees get 403 from the employee list, can reach their own record through an individual-resource endpoint; Managers own record + direct reports; HR Admin the company; Super Admin cross-company deferred. D3: the permission catalog (employees 7, departments 3, roles 2, system 1; unknown/missing permissions deny). D4: system roles `employee`, `manager`, `hr_admin`, `super_admin` (CompanyId NULL, IsSystem), legacy company administrator → hr_admin, ids preserved, idempotent and non-destructive. D5: permissions loaded from the database on every protected request. Out of scope and not done: employee/department/role-management endpoints, audit writer, change-password, offboarding, any JWT/BCrypt/email/domain change.

### What was done
1. Inspected `Role`/`User`/`Employee` entities and configurations, both seeders, `AuthController`, login and `/me` handlers/DTOs, `Program.cs`, `TenantResolver`, the migrations and the existing tests that depend on the old `/me` contract.
2. Domain `Authorization` namespace (catalog, strict permission parser, system-role definitions with fixed ids, `DataScope`); Application interfaces (`ICurrentUser`, `ICurrentUserLoader`, `IDataScopeService`, `IReferenceValidator`); Infrastructure services (`CurrentUserService`, `DataScopeService`, `ReferenceValidator`, `SystemRoleSeeder`); API attributes + global `PermissionAuthorizationFilter`; both seeders switched to the shared `hr_admin` role; login/`/me` DTOs return a stable role key and a permission object.
3. Migration `AddSystemRoles` scaffolded (it only produced the unique index), then the seed, the legacy-Admin repoint, the guard and an explicit Down were written by hand. `dotnet ef migrations script` output read before accepting it. `has-pending-model-changes`: none.
4. Tests written (see CHANGELOG). The migration was tested only through the EF Core runner on scratch databases inside the disposable Testcontainers instance, against representative legacy data (two companies with legacy `"Admin"` roles, two admins each, a custom-role user, an employee record), including Down.
5. **Result of the first focused run:** 100/100 (authorization + migration + seeder + session tests). **Mutation check:** the global filter registration was commented out in `Program.cs` (backed up first, restored and confirmed afterwards): the authorization tests failed, as intended.
6. **First complete run: 5 failures, all existing tests whose expectations were written before the system roles existed** (a hard-coded list of four migrations; four role-count assertions in `CompanyDomainTests`, e.g. "exactly one role"). Updated to the new, intended state (four shared system roles, no per-company role, five migrations); no assertion was removed or loosened beyond that. **Final: 328 tests — 328 passed, 0 failed, 0 skipped** (TRX: total 328, executed 328, NotExecuted 0), solution build 0 warnings / 0 errors, `has-pending-model-changes`: no changes.

### Safety
Only the guarded Testcontainers environment. No existing database, Neon, `hrms_db`, `hrms_migration_test` or the native server was contacted; the migration was not applied to any persistent database; no `HRMS-Backend` edit, Robocopy, commit, push or deploy; no secrets read or printed. The shell tool reported one failed heredoc (nothing written) – files were created with the file-write tool instead.

### Decisions taken inside the approved scope (review these)
- **No EF global query filter.** Company isolation is enforced explicitly (every scope query starts with `CompanyId == current`, `CurrentUserService` verifies role/employee company consistency). A global filter would have changed `HrmsDbContext`'s construction and every seeder/migration/test path; it can be added later.
- **Legacy `"Admin"` role rows are kept** (unused) rather than deleted, to stay non-destructive; Down relies on them.
- **Which role gets which permission** is an interpretation of the spec's role hierarchy plus D3: `roles.view` and `roles.manage_permissions` are granted to Super Admin only; `system.configure_company` to HR Admin and Super Admin; `departments.view_all`/`manage` to HR Admin and above; `employees.edit_own` to every role. Not stated verbatim in the specification — confirm.
- Roles are looked up by `Name` as the stable key; legacy company roles keep their name (e.g. `Admin`) as the key until reassigned.
- Logout deliberately works for a deactivated account (`[SelfService(requireActiveAccount: false)]`); `/me` requires an active account.

### Unresolved specification issues (unchanged, not decided here)
D2 Super Admin across tenants; D6 failed-login audit with a required `user_id`; D7 self-edit/profile fields; D8 department delete/bulk reassignment/company settings; D9 temporary password and email transport; D10 manager-deactivation rule; JWT `permissions` claim in the spec vs database-per-request (D5 chosen); `GET /employees` contradiction in the spec (resolved by D1).

### Remaining risks
Deactivation is now enforced per request by the filter for permission-protected and `me` actions, but an action marked `[AllowAnonymous]` or the logout action does not check it; the migration is untested at production volume or on Neon; the migration runs automatically at the next start of any environment; `jsonb` permissions are edited only by SQL until the role-management endpoints exist.

---

## Session 18 — 2026-10-21 — Read-only verification of the authorization foundation, then the `[AllowAnonymous]` bypass fix

### Verification (read-only, no files changed)
Re-read the final code. Confirmed there is **no EF global query filter** (`HasQueryFilter` appears nowhere; the earlier plan that mentioned one was not implemented) and that isolation is explicit in `CurrentUserService`, `DataScopeService`, `ReferenceValidator` and `UserRepository`. Found one real defect: the global filter returned early on any `IAllowAnonymous`. Other gaps reported (not fixed here): scope ignores employee status; no production write path to test cross-company rejection; no scope helper for create/deactivate; a company role named `hr_admin` is not prevented; Swagger is unauthenticated; permission grants for `edit_any`, `roles.view` and the department permissions are my inference (the spec matrix backs the employee/system/roles rows).

### Fix (approved scope)
1. Inspected the filter, attributes, convention test and the controllers (only `AuthController` and `HealthController` exist).
2. Wrote the regression tests and test-only probe controllers **first** and ran them against the unmodified filter: 7 of 32 failed, reproducing the vulnerability (anonymous and permission-less callers got 200 from `[RequirePermission]`/`[SelfService]` actions under a controller-level `[AllowAnonymous]`).
3. Added `AuthorizationDeclaration.Classify` and switched the filter and the convention test to it (details in `CHANGELOG.md`). Conflicting metadata is denied for everyone; it is never treated as permission to bypass.
4. One of my own test expectations was wrong (an anonymous call to a permission+self-service conflict is rejected by the authentication layer with 401 before the filter runs); it now accepts 401 or 403 for anonymous and still requires 403 for authenticated callers. Not a production change.
5. The ASP0026 analyzer warnings caused by the intentionally wrong probes were suppressed around the probes only.
6. **Results:** focused authorization/auth tests 135/135; full suite **355 tests — 355 passed, 0 failed, 0 skipped** (TRX executed 355, NotExecuted 0); `dotnet build HRMS.slnx` (no-incremental) 0 warnings / 0 errors; `has-pending-model-changes`: none.

### Safety
Only the guarded Testcontainers setup. No Neon, native PostgreSQL, `hrms_db` or `hrms_migration_test` access; no migration created, changed or applied; permission catalog, role grants, JWT, session validation, tenant resolution untouched; no `HRMS-Backend` change, Robocopy, commit, push or deploy.

---

## Session 19 — 2026-10-22 — Audit logging infrastructure

### Inspection (before any code)
Read the specification's audit sections (Table 7, workflow step 14, section 4 step 3, section 7, the Phase 1 checklist and index list) and the code: `AuditLog` entity and configuration (jsonb old/new values, VARCHAR(45) IP, RESTRICT FKs, `(CompanyId, CreatedAt)` index), the `AuditLogs` creation and the jsonb conversion in the migrations, `IClientInfo`/`HttpClientInfo`, `ICurrentUser`, `ITenantContext`, the login handler, `HrmsDbContext`, repositories and the existing tests. Confirmed that no audit service, writer or usage existed (only the entity, table and configuration). The table already matches Table 7, so no migration was needed.

### Requirements taken from the specification (nothing added)
Fields of Table 7; dotted action names; written after permission and scope checks; append-only/immutable; failed login attempts logged with IP (section 4, step 3); audit on every create/update/delete (checklist) - those operations do not exist yet.

### Ambiguities (not resolved by me)
1. **Failed login for an email that does not exist:** step 2 says only "401"; step 3 says to log the failure after a password mismatch, when the user exists. `audit_log.user_id` is a required foreign key, so such an attempt cannot be stored without a schema change (nullable `user_id`, or a system user). Not done; needs a decision (this is D6).
2. Whether **successful logins, logouts, session deletion, deactivated-account rejections** are audited: the specification restricts audit to create/update/delete actions and failed attempts; not audited.
3. **Module names** for Phase 1 beyond the spec's examples (`employee`, `leave`, `attendance`, `payroll`): none were invented; only `auth` exists as a constant.
4. Whether failures to write an audit entry for a *rejected* request should fail the request: chosen - no (401 stays 401, Error logged); for business changes the entry is atomic with the change instead.
5. Is "append-only" required at the database level? The specification says records are never updated or deleted; an application-layer guard is implemented, a database trigger/privilege control is not.

### Implementation
`AuditPayload`, `AuditEntry`, `IAuditWriter` (Application); `AuditWriter` (Infrastructure); append-only guard in `HrmsDbContext.SaveChanges*`; registration in `Program.cs`; failed-login wiring in `LoginCommandHandler` (details in `CHANGELOG.md`). Design decisions: the staged entry lives in the request's DbContext so it shares the business change's transaction; the unauthenticated path is a narrow, tenant-bound method writing through its own context; the entry type has no actor/company/IP members.

### Verification
- First complete focused run: 92/92 audit tests green. **Mutation check:** the append-only guard, the secret exclusion and the login wiring were each disabled in a temporary copy: 39 of the 92 tests failed (29 caught by the secret-exclusion mutation incl. the entry snapshot test, 5 by the login-wiring mutation, 4 by the append-only mutation, and 1 writer test that also asserts secrets are absent from the stored row); the three files were restored from backups and verified byte-identical with `cmp`.
- Clean rebuild: 0 warnings / 0 errors. Full suite: **447 tests - 447 passed, 0 failed, 0 skipped** (TRX executed 447, NotExecuted 0), up from 355. `has-pending-model-changes`: no changes; five migrations unchanged.

### Safety
Only the guarded Testcontainers environment. No Neon, native PostgreSQL, `hrms_db` or `hrms_migration_test` access; no migration created or applied; JWT, sessions, BCrypt, tenant resolution, system-role migration and the permission catalog untouched; authorization regression tests unchanged and passing; no `HRMS-Backend` change, Robocopy, commit, push or deploy. Temporary mutation backups were kept in `/tmp` (outside the project).

### Remaining risks
Only one event is wired (failed login). Every future create/update/delete must call `Stage` before its `SaveChanges`; there is no automatic enforcement, so each feature needs a test that its audit row exists (and that a rolled-back change leaves none). Unlimited failed logins can grow the table (no rate limiting - known issue 5). Database-level immutability is not enforced. The failed-login row adds a database write only for existing accounts, which widens the existing account-enumeration timing difference. Sensitive-name matching is name- and shape-based: a credential stored under an innocent name in a non-matching format would not be removed, so payloads must be built from explicit DTOs rather than raw entities where possible.

---

## Session 20 — 2026-10-23 — Transaction pattern for data-changing handlers

### Inspection
No transaction abstraction or behavior existed: `BeginTransaction` was nowhere in production code, `ValidationBehavior` was the only MediatR behavior. `SaveChanges` is called by `SessionRepository` (login, logout), the seeders, `SystemRoleSeeder` and the independent failed-login write; `ExecuteUpdate` by `UserRepository.UpdateLastLoginAsync`. Login and logout are not audited business changes and were left unwrapped.

### Design decisions (all inside the requested scope)
1. Opt-in by marker interfaces rather than "wrap every command", so queries and the auth endpoints are untouched and nothing is wrapped silently.
2. The unit of work uses the request's own `HrmsDbContext`, so repositories need no change: their saves and bulk updates automatically use the open transaction.
3. A transaction that is already open is joined, never committed or rolled back by the inner level.
4. After a rollback the change tracker is cleared (otherwise rolled-back entities stay "Unchanged" and staged audit rows stay pending and would be written by the next save in the same request).
5. `IAuditedRequest` makes the pipeline refuse to commit unless an audit entry was staged. It is *enforcement for marked requests only*; I do not claim that every future handler is audited. A handler that implements neither marker is neither transactional nor audit-checked.
6. `StagedCount` is exposed on a separate interface (`IAuditStagingState`) so the existing `IAuditWriter` contract test (only `Stage` and `RecordFailedLoginAsync`) stays valid and unchanged.

### Verification
First focused run 22/22. Mutation check (explicit transaction removed: 14 failures; audited check removed: 1; tracker clear removed: 1), then restored and verified identical with `cmp`. Clean rebuild 0 warnings / 0 errors; full suite **469 tests — 469 passed, 0 failed, 0 skipped** (TRX executed 469, NotExecuted 0), up from 447; `has-pending-model-changes`: no changes; five migrations unchanged.

### Safety
Only the guarded Testcontainers environment; no Neon, native PostgreSQL, `hrms_db` or `hrms_migration_test` access; no migration; authorization, authentication, sessions, tenant resolution, role migration and audit sanitization untouched; no `HRMS-Backend` change, Robocopy, commit, push or deploy. Temporary mutation backups were in `/tmp`.

### Limitations / still explicit
No employee, department or role endpoints exist, so the pattern is exercised only by test handlers. A handler must implement the marker (nothing detects a data-changing command that forgot it); the audit check verifies presence, not correctness. Work outside the request's `HrmsDbContext` (another context, raw connections, the independent failed-login path, external calls such as email) is not in the transaction - side effects like sending a welcome email must happen after the commit or be made idempotent. If Npgsql retries are ever enabled the whole handler would re-run inside the execution strategy and must be idempotent. Database-level immutability of the audit table and the forwarded-headers IP issue (previous review) are still open.

---

## Session 21 — 2026-10-24 — Declared audit actions, request convention test, commit cancellation

### Inspection
Re-read `IAuditedRequest`, `TransactionBehavior`, `IAuditStagingState`, `AuditWriter`, `AuditEntry`, `EfUnitOfWork`, and listed the actual production requests: `LoginCommand`, `LogoutCommand`, `MeQuery` (no notifications or stream requests).

### Decisions
1. **Attribute, not interface members, for the declaration.** `[AuditExpectation(module, action)]` on the command type lets the convention test read every command's declaration by reflection without constructing instances, is repeatable (multi-entry commands later), and the behavior fails closed if it is missing. The cost is that the compiler does not force it; the runtime check and the convention test do.
2. **A window, not a count.** The behavior notes `Staged.Count` when the command starts and only looks at entries after it; each declaration consumes one distinct entry. Entries staged earlier in the request cannot satisfy it. An enclosing command's window includes what its inner commands staged (documented).
3. **No target or value check.** Module/action matching is all that is verified; this is stated in the contract, the changelog and the docs, not implied.
4. **Allow-list for login/logout with reasons based on their handlers**, not a blanket exemption; login's two non-atomic writes are recorded as a known gap rather than hidden or silently "fixed" here (out of scope).
5. **Commit uses `CancellationToken.None`** after a successful flush; flush and handler stay cancellable. Disposal moved into a `finally` that ignores its own failure so it cannot replace the original exception; this branch cannot be exercised with the available EF hooks and is therefore untested (stated in the changelog).

### Verification
First focused run 146/146 (transaction + audit folders). Mutation check: 5 mutations, each caught (commit cancellable: 1 failure - so the cancellation test is not vacuous; window ignored: 3; module ignored: 1; entries not consumed: 1; undeclared check removed: 1); files restored and verified identical with `cmp`. Clean rebuild 0 warnings / 0 errors; full suite **501 tests - 501 passed, 0 failed, 0 skipped** (TRX executed 501, NotExecuted 0), up from 469; `has-pending-model-changes`: none; five migrations unchanged.

### Safety
Only the guarded Testcontainers environment; no Neon, native PostgreSQL, `hrms_db` or `hrms_migration_test` access; no migration; authorization, tenant resolution, JWT, sessions, role migration and audit sanitization untouched; no `HRMS-Backend` change, Robocopy, commit, push or deploy. Mutation backups were kept in `/tmp`.

### Remaining limitations before employee creation
No employee/department/role handlers exist; the pattern is exercised by test handlers only. Declared module/action does not prove target, values or that a change happened (each command's tests must assert the row). Side effects outside the request's DbContext (welcome email, D9) are still not part of the transaction and there is no post-commit hook. Concurrent auto-generated employee codes would collide on the unique index (the loser rolls back cleanly but needs a retry or sequence). Default isolation does not serialize rules like D10. Login's two writes are not atomic. Database-level audit immutability, the proxy IP behind Render and decision D6 remain open.

---

## Session 22 — 2026-10-25 — Phase 1 Employee Management

### Inspection
Read the specification (5.1 onboarding, 5.2 offboarding, 4.1 session invalidation, Table 3/4, the checklist, testing checkpoints and the API table) and verified the actual code instead of the earlier reports: `Employee`/`User`/`Department`/`AuditLog` entities and configurations, the unique indexes (`IX_Employees_CompanyId_EmpCode`, `IX_Users_CompanyId_Email`, `IX_Employees_UserId`), the permission catalog and system-role grants, `IDataScopeService`, `IReferenceValidator`, `ICurrentUser`, `IAuditWriter`, `TransactionBehavior`, `IAuditedRequest`/`[AuditExpectation]`, `RequestConventionTests`, the exception middleware (no 404/409 mapping existed), and the only existing production requests (login, logout, me). No schema change was required.

### Decisions and assumptions (each needs approval; none was in the specification)
1. **Temporary password / welcome email (D9) — blocking for a usable create.** The server generates a random password, stores only its BCrypt hash, sets `IsFirstLogin`, and does not return, log, audit or deliver it. Created accounts therefore cannot log in until a delivery mechanism and a password-reset/change path exist. I did not invent a delivery channel (returning the password in the response would be a new credential exposure).
2. **Employee-code format:** the specification defines it (`EMP-0001`, auto-generated, unique per company). Concurrency: transaction-scoped PostgreSQL advisory lock per company + highest numeric code + 1; unique index as final guard; no retry loop, no migration. PostgreSQL-specific.
3. **List requires `employees.view_team`** (so a custom role with only `view_all` cannot list); scoping then uses the strongest view permission. Single-record reads need `view_own` and answer 404 (not 403) for anything out of scope, foreign or missing, to avoid an existence oracle.
4. **Update is HR-only (`edit_any`)**: the specification says "HR/Self" but defines no self-editable fields in Table 4 (D7), so no self-edit exists. PUT is a full replacement of name, department, manager, designation, joining date, status and last working date; email, code, role and company are immutable here. Statuses settable: active, probation, contract, notice_period; `inactive` only by deactivate; a deactivated employee is read-only (no reactivation is specified). Last working date required for notice_period and forbidden for the other three. Date sanity bounds 1900..2100. Managers must be non-inactive; self-manager and cycles rejected.
5. **Deactivate** follows the checklist and the testing checkpoint ("login immediately blocked, all sessions killed"): inactive + last working date + account blocked + sessions deleted. The spec's "notice_period or inactive" option is exposed through PUT (notice_period) and deactivate (inactive). Direct reports are reported, not reassigned or blocked (D10). Self-deactivation refused. Pending-request closing is not applicable until Phase 2 exists.
6. List defaults: page 1, pageSize 20 (max 100); search over name/code/designation/email; fixed sort whitelist; status sorts by its stored code order.
7. No optimistic concurrency (last write wins) — no row-version column exists and none was added.

### Implementation
Application: `EmployeeContracts` (DTOs, requests, status names), `EmployeeRules`, three audited commands with validators and handlers, two queries, `IEmployeePersistence` interfaces, `NotFoundException`, `ConflictException`. Infrastructure: `EmployeeRepository` (company-bound, conflict translation), `EmployeeQueries` (scope-based reads), `EmployeeCodeGenerator`. API: `EmployeesController` (thin; one `[RequirePermission]` per action), exception mapping for 404/409, DI. Tests: seven new classes plus shared `EmployeeApiTestBase`. Existing tests and production behavior of auth, authorization, sessions, tenant resolution, role migration and audit sanitization were not changed.

### Verification
1. Focused runs while building: create + read 66/66, update + deactivate 39/39, concurrency 22/22 (with deactivate), architecture/contract/convention 36/36.
2. **Mutation checks** (each restored and verified byte-identical with `cmp`): advisory lock removed → 3 failures; company filter removed from the write lookup → 1; department validation removed → 2; session deletion removed → 3; read scope removed → 3; search escaping removed → 3.
3. **First full run: 629 passed, 1 failed.** The failure was `UserEmailConstraintTests.Different_companies_may_use_the_same_email_address` (existing, unchanged): it counts users by the literal address `shared@example.test` across the whole shared test database, and my new `The_same_email_in_another_company_is_allowed` created two users with that address. Genuine test interference caused by my test, not a product defect; fixed by giving my test a unique per-run address. The existing test was left as is (it remains sensitive to other tests using that literal address).
4. Clean rebuild (`--no-incremental`): 0 warnings / 0 errors. Final full run: **630 tests — 630 passed, 0 failed, 0 skipped** (TRX executed 630, NotExecuted 0), up from 501. `has-pending-model-changes`: no changes; five migrations unchanged.
5. Changed-file review (files newer than the previous status update, excluding build output): only the intended Application/Infrastructure/API/test files listed above, plus docs; no unrelated modification. `docker ps -a` showed the three pre-existing containers untouched plus the transient Testcontainers reaper.

### Safety
Only the guarded Testcontainers environment (random database name, marker row, guard before every operation). No Neon, native PostgreSQL, `hrms_db` or `hrms_migration_test` access; no migration created or applied; no `HRMS-Backend` change, Robocopy, commit, push or deploy; no secret read or printed. Mutation backups were kept in `/tmp`.

### Remaining limitations / open decisions
D9 (credential delivery) makes created accounts unusable until resolved; first-login enforcement and change-password are not implemented; D7 (self-edit and profile fields: phone, personal email, employment type, address, bank, PAN/Aadhaar are not in Table 4); D10 (manager deactivation) and automatic reassignment of reports; D2 (Super Admin across companies: a Super Admin has company-wide access only to their own company); D6 and D8 unchanged. Roles other than `employee` cannot be assigned through these endpoints (role assignment belongs to role management, which is not implemented), so nothing in this module can create a manager or HR account. No audit read endpoint exists. The advisory-lock strategy is PostgreSQL-specific. Database-level audit immutability, the proxy IP behind Render and login's non-atomic session write are unchanged.

---

## Session 23 — 2026-10-26 — Review of the employee module and decision audit

Method: read the final source of all five endpoints, handlers, validators, repositories, queries, permission attributes, audit logic and tests; wrote a failing test for each suspected defect before changing code; interleaved two database connections deterministically for the concurrency review; re-read specification sections 4.1, 5.1, 5.2, Tables 3–4, the checklist, the testing checkpoints and the API table.

### 1. Findings by severity

| # | Severity | Finding | Status |
|---|---|---|---|
| 1 | High (data integrity, audit accuracy) | **Lost update / resurrection.** `Employee` had no concurrency token. A request that read an employee, then lost a race with another administrator, overwrote the winner's change, could record a wrong "before" in the audit row, and — demonstrated — could change a just-deactivated employee back to `probation` while the account stayed blocked. 4 interleaving tests failed against the old code. | **Fixed** |
| 2 | Medium | **NUL (U+0000) in name, designation or search text → HTTP 500** (PostgreSQL rejects it). | **Fixed** (400) |
| 3 | Medium (functional) | **Deactivated manager froze their reports:** the "manager must be active" rule also ran for an unchanged manager, so no report of a deactivated manager could be edited until HR reassigned them. | **Fixed** |
| 4 | Low | `page=2147483647` → int overflow → negative OFFSET → 500. | **Fixed** |
| 5 | Low | Deactivation audit "after" was nested while "before" was flat (hard to diff). | **Fixed** |
| 6 | Low | Swagger documented query parameters in PascalCase while bodies are camelCase. | **Fixed** |
| 7 | Medium, **not fixable without a decision** | A *stale browser form* still overwrites a change committed before it was submitted: the API has no `If-Match`/ETag precondition (E1). The server-side token only protects the read-modify-write inside one request. | Open (E1) |
| 8 | Medium, business | Created accounts cannot log in: the generated temporary password is never delivered and no change-password/first-login enforcement exists (D9). | Open (D9) |
| 9 | Medium, spec gap | The specification's create form lists personal email, phone and employment type; Table 4 has no columns for them, so they are neither accepted nor stored (D7). | Open (D7) |
| 10 | Low, scope | List filters (status, department), sorting and the choice of searched fields are not in the specification (it says "pagination and search"). They are validated and whitelisted, but they were added by me (E2). | Open (E2) |
| 11 | Low, theoretical | Code generator: after `EMP-999999999` the next code (10 digits) would not match the `{1,9}` digit pattern, so numbering would repeat and the unique index would answer 409 forever. Unreachable in practice; not changed. | Documented |

Verified correct, no change needed: code-generator locking (a transaction-scoped advisory lock per company, taken only inside the request transaction, released at commit or rollback; the highest numeric `EMP-n` is computed after the lock so it sees every committed code; non-matching codes are ignored; `highest + 1` fits an int; the unique index is the final guard and its violation becomes a 409 without database detail); tenant isolation on all five operations (company-bound loads and queries, reference checks, identical 404/400 answers for foreign and missing rows); permission and scope enforcement; audit target, actor, company, IP and before/after values; transaction rollback; error bodies and Swagger contract.

### 2. Concurrency review (phase 3)
- Within one request the flow is load → modify → save. Without a token two requests could interleave there. Fix: `Employee.Version` mapped to PostgreSQL's `xmin` with `IsRowVersion()`; EF then issues `UPDATE ... WHERE "Id" = @id AND xmin = @loaded`, a lost race affects 0 rows, and the save throws `DbUpdateConcurrencyException`, translated to a 409 (the transaction rolls back, the winner's change and audit row stand). No stored column is added.
- **Migration justified and snapshot-only:** the snapshot must record the mapping or EF 9+ throws on `Migrate()` (both seeders call it). EF scaffolds `AddColumn "xmin"`, which would fail, so `Up`/`Down` are empty by design. Read the generated SQL (only the history insert) and tested on a throwaway database with employee data through the EF runner (schema and data identical, rollback and re-apply harmless). Not applied to any persistent database.
- Only the `Employees` row carries the token; `Users` does not, because the login's `LastLogin` bulk update changes `xmin` and would cause spurious conflicts. A change that touches only `Users.Name` (name-only edit) while the employee row is unchanged therefore merges without a conflict — harmless, and the audit shows it.
- Not done on purpose: `If-Match`/ETag (E1), retries of conflicting requests.

### 3. Deactivation and account lifecycle (phase 4) — verified
Deactivation deletes every session of the user, sets the account inactive and the employee `inactive` in one transaction with the audit entry. Consequences, each tested: the old token returns 401 (no session); login returns 403 after a correct password; and — the new test — even a valid signed token with a *fresh session row* (what a login racing the deactivation could leave behind) gets 403 on list, get, create, update, deactivate and `/me`, because `CurrentUserService` re-reads `IsActive` on every protected request (the test fails when that check is disabled). Such a token can only call logout. The JWT stays cryptographically valid until it expires, and an unused session row can remain until expiry; neither grants access. Reactivation, manager reassignment and other lifecycle workflows were not implemented because the specification does not define them.

### 4. Test quality and isolation (phase 5)
The employee tests assert persisted state by reading the guarded PostgreSQL through fresh connections (rows, sessions, audit entries, counts) and use real parallel requests and real two-connection interleavings; the only substitutions are switches that make a collaborator fail (audit writer, code generator, password generator) to prove atomic rollback. Each test creates its own company, and every query helper filters by company. Shared-data hazards found: `UserEmailConstraintTests.Different_companies_may_use_the_same_email_address` counts users with the literal address `shared@example.test` across the whole shared database, which broke once when a new test used the same literal. My tests no longer use it; the existing test was **not** changed (recommendation: scope its count to its two company ids). No other test counts rows globally by a literal. Not covered: lone surrogate characters in JSON strings (the JSON parser rejects them before the module sees them — assumed, not tested).

### 5. Does the specification support each current assumption? (phase 2)

| Behavior | Supported by the document? |
|---|---|
| Create: status `active`, default role `employee`, code `EMP-0001…`, work email = username, `IsFirstLogin`, hashed temporary password | **Yes** (5.1 steps 2–5, Tables 3–4) |
| Temporary password delivery by email | Named in 5.1 step 4; transport and templates undefined (D9) |
| Personal email, phone, employment type in the create form | In 5.1 step 1 but **no columns** in Table 4 (D7) — not implemented |
| `GET /employees` scoped by role, with pagination and search; Employee gets 403 | **Yes** (checklist, API table, testing checkpoints) |
| Which fields search covers; status/department filters; sorting | **No** (E2) |
| `GET /employees/{id}` scoped | Yes; answer for out-of-scope (404 vs 403) not specified (E3) |
| `PUT /employees/{id}` by "HR/Self" | Partly: which fields a user may edit is not defined (D7); full-replacement semantics, allowed statuses, last-working-date rules, 1900–2100 date bounds not specified (E4, E5, E7) |
| Deactivate: HR only, inactive, last working date, sessions deleted, login blocked, record kept | **Yes** (4.1, 5.2 steps 1,2,5, checkpoints). How `notice_period` is entered is ambiguous (5.2 step 1 says "notice_period or inactive") (E5) |
| Closing pending leave/mispunch/timesheet requests | Yes, but those tables are Phase 2 — deferred |
| Direct reports "must be reassigned … by HR" | Stated (5.2 step 6) without a mechanism or timing (D10) |
| Manager must not be inactive; no reporting cycles; no self-manager; no self-deactivation; no reactivation | **No** — my integrity/safety assumptions (E7, E6) |
| Audit on create/edit/deactivate | **Yes** (checklist, testing checkpoints) |
| Super Admin "sees all companies" | Stated in the testing checkpoints; **not implemented** (D2) |

### 6. Decision register — product-owner approval needed
Each entry: question → options → consequences → recommended default → dependent modules.

**D2 — Super Admin across companies.** *How does a platform-level Super Admin authenticate and work on more than one company, given that the tenant is resolved from the request host and every query is company-bound?* (a) A dedicated platform area (own host/company, endpoints that take an explicit company id, separate permission checks); (b) a Super Admin token accepted on any tenant host with the company chosen by the host; (c) keep Super Admin per company (current: same rights as HR plus roles/permissions, limited to its own company). Consequences: (a) clean isolation, one new surface to secure; (b) weakens "company claim must match host", every service must handle an unscoped user; (c) the checkpoint "sees all companies" and "Create / manage companies" are not met. **Default: (a).** Depends on: company management, role/permission management, cross-company views, audit reading, every scope service.

**D6 — failed login for an email that does not exist.** *Must it be audited?* The spec logs a failed attempt only after a password mismatch (user exists); `audit_log.user_id` is required. (a) Do not persist (current) and write a structured security log without the email; (b) make `user_id` nullable with a check that only specific actions may omit it; (c) a non-login "system" user per company. Consequences: (a) no schema change but no audit trail of probing; (b) one migration, weakens an integrity guarantee on an immutable table, stores data about strangers; (c) extra privileged-looking rows. **Default: (a), revisit (b) with rate limiting.** Depends on: authentication, audit, compliance reporting.

**D7 — self-edit and profile fields.** *Which fields may an employee edit on their own record, and where are phone, personal email, employment type, photo, address, emergency contact, bank details and PAN/Aadhaar stored?* (a) New one-to-one profile table with those fields (migration), self-edit limited to profile fields, HR edits employment fields; (b) only a small subset now; (c) defer. Consequences: (a) sensitive data needs masking/encryption and extra audit redaction; the create form can finally be complete; (b)/(c) leave "HR/Self" unmet. **Default: (a).** Depends on: employee update, "My profile" page, payroll, audit sanitizer, create form.

**D8 — departments.** *What happens when a department that has employees or child departments is deleted? How is "bulk reassign" requested? What is "configure company settings"?* (a) Block deletion while employees/children exist (409) plus an audited bulk-reassign endpoint; (b) soft-delete; (c) cascade. Company settings: the spec lists the permission but no setting. Consequences: (a) simple and safe; (c) data loss. **Default: (a); defer company settings until its fields are defined.** Depends on: departments, employee update, org tree, head-of-department scope.

**D9 — temporary password delivery.** *How does a new employee receive credentials?* (a) Email the temporary password through a provider using a post-commit/outbox step; (b) show it once to HR in the create response; (c) invitation email with a single-use expiring link (departs from the text). All need first-login enforcement and change-password. Consequences: (a) matches the spec, needs provider configuration, secret handling and retry/outbox; (b) credential in an HTTP response and logs risk; (c) safest for secrets but a spec change. **Default: (a) with the outbox, (b) only behind a non-production flag meanwhile.** Depends on: change-password, first-login behavior, email infrastructure, audit redaction, employee creation.

**D10 — deactivating a manager who has direct reports.** (a) Allow and report the count (current); (b) block until reports are reassigned; (c) require a `reassignTo` manager in the request and move all reports atomically (audited per report); (d) auto-reassign to the manager's manager. Consequences: (a) reports point at an inactive manager; approvals and team scope degrade; (b) unambiguous but obstructs offboarding; (c) one step, needs a target; (d) silent organizational change. **Default: (c), with an explicit "leave unassigned" option.** Depends on: Phase 2 approval queues, team scope, employee update.

**E1 — concurrency precondition on PUT.** *Should an update carry the version it was based on?* (a) Required `If-Match` ETag (428 when missing, 412/409 on mismatch); (b) optional — enforced when sent; (c) none (current). Consequence of (c): a stale form silently overwrites. **Default: (b) now, (a) once the frontend sends it.** Depends on: employees, departments, roles updates.

**E2 — list filters/sorting.** Keep status and department filters and the sort whitelist, or remove them? **Default: keep (validated, whitelisted), formally approve; removal is small.** Depends on: employee list page.

**E3 — out-of-scope single read.** 404 (current, no existence oracle) or 403. **Default: 404.**

**E4 — update semantics.** Full replacement (current) or partial update; whether `name` belongs to the employee or the account. **Default: keep PUT; add PATCH only if the frontend needs it.**

**E5 — status model.** Allowed transitions; how `notice_period` is entered (currently via update, with a required last working date); overlap between the status values `probation`/`contract` and the form's employment type full-time/part-time/contract. **Default: keep status as the lifecycle and add employment type in D7's profile table.**

**E6 — reactivation.** Not specified, not implemented (deactivated employees are read-only). **Default: defer; if required, an explicit audited `reactivate` endpoint that issues a new temporary password.**

**E7 — guard rules I added** (not in the specification): manager must not be inactive (only when the manager changes), no self-manager, no reporting cycles, no self-deactivation, dates within 1900–2100, last working date not before the joining date and tied to `notice_period`. **Default: keep; they only reject clearly invalid states.** Not added: protecting the last HR admin.

**E8 — role assignment.** Nothing can change a user's role (`employee` on create; `manager`/`hr_admin` only via seeding). **Default: a separate audited role-assignment endpoint (HR/Super Admin) in the role-management module.** Depends on: manager scope, every manager-only feature.

### 7. Verification
Failing tests first (paging, NUL, deactivated manager: 6 of 9 failed; concurrency: 4 of 4 failed), then fixes. Focused runs: regressions/concurrency/deactivate/update 52/52; migration tests 13/13; all employee tests 155/155. Mutation check: per-request active-account check disabled → the lifecycle test fails (file restored, verified with `cmp`). Clean rebuild: 0 warnings / 0 errors. Full suite: **648 tests — 648 passed, 0 failed, 0 skipped** (TRX executed 648, NotExecuted 0), up from 630. `has-pending-model-changes`: none; six migrations (the new one is snapshot-only).

### 8. Safety
Only the guarded Testcontainers environment; no Neon, native PostgreSQL, `hrms_db` or `hrms_migration_test`; the new migration was applied only to throwaway databases inside the test container; no `HRMS-Backend` change, Robocopy, commit, push or deploy; no secrets read or printed. Departments and every later module were not started.

### 9. Separation of status
- **Implemented and tested:** the five endpoints, optimistic concurrency, input hardening, lifecycle enforcement, audit.
- **Known technical limitations:** PostgreSQL-specific code allocation; no `If-Match`; name-only edits do not conflict; code space ends at 999,999,999; the unrelated fragile global-count test; database-level audit immutability and proxy IP unchanged from earlier sessions.
- **Unresolved product decisions:** D2, D6, D7, D8, D9, D10, E1–E8.
- **Deferred to later modules:** change-password and first-login enforcement, email delivery, departments API, role management and assignment, profile fields, closing Phase 2 requests on offboarding.

---

## Session 24 — 2026-10-27 — Phase 1 completion foundations

Method: re-read the specification (4 login/session/deactivation flow, 5.1 onboarding, 5.2 offboarding, 5.3 departments, Tables 3 and 4, the Phase 1 checklist, the testing checkpoints and the API table), inspected the actual code (login, `IsFirstLogin`, sessions, `ICurrentUser`, the authorization filter, the employee module, the unit of work, the audit writer), then implemented only what the document defines or what the task explicitly directed, tests first where a defect was suspected.

### 1. Specification findings (what the document actually says)
- **Passwords:** Table 3: `password_hash` = bcrypt cost 12+; `is_first_login` "TRUE = force password reset on next login". Checklist: `POST /api/auth/change-password — enforce on first login`; frontend: "Force password reset screen: shown before any page on is_first_login = true". API table: "Change password (required on first login)". **No password policy** (length, composition, expiry, history) and **no forgot/reset-password flow** exist anywhere in the document.
- **Onboarding (5.1):** step 1 form fields: full name, personal email, work email, phone, department, designation, joining date, assigned manager, employment type (full-time / part-time / contract). Step 2: user row with `bcrypt(temp_password)`, default role employee, `is_active = true`. Step 4: "Email to employee: login URL, username, temporary password. Employee must reset password on first login." Step 5: `is_first_login` → redirect to the reset screen. Step 6: employee adds profile photo, permanent address, emergency contact name + phone, bank account details, PAN / Aadhaar. No email provider, template or URL is defined.
- **Table 4 (`employees`):** no personal email, phone or employment type column; step-6 data has no table at all.
- **Roles:** "A user has exactly one role at a time"; Super Admin "manages all admins"; matrix: "Manage roles & permissions" = Super Admin only; `GET/PUT /api/roles` and `PUT /api/roles/:id/permissions` are Super Admin only. **Nothing says who may change a user's role (`users.role_id`) or how.**
- **Offboarding (5.2):** step 6: direct reports' `manager_id` "must be reassigned to another manager by HR" — after the account is blocked, with no mechanism or timing.

### 2. Decision register after this session
For each: what the document says → explicit vs assumption → what depends on it → recommended default → can work proceed without approval?

**D9 — credential delivery. PARTLY IMPLEMENTED, approval needed.** *Document:* temporary password by email (5.1 steps 2/4). *Explicit:* the server creates a hashed credential, forces a reset, and emails the person. *Assumption (mine, directed by the task):* a one-time token instead of a permanent/temporary password; 72-hour lifetime; reissue only before the first password is set; hash storage. *Depends on:* employee creation, onboarding, any future "welcome email", frontend set-password page. *Default:* keep the token flow; choose a provider and a URL template, implement `IAccountInvitationSender`, later add an outbox for retries. *Proceed without approval?* The secure foundation is done and safe; **delivery cannot proceed without a provider decision** (and approval of the deviation from "temporary password").

**D7 — profile fields and self-edit. PARTLY IMPLEMENTED, approval needed.** *Document:* the create form lists personal email, phone, employment type; "PUT /employees/:id — HR/Self"; step 6 lists self-completed profile data; none of it is in Table 4. *Explicit:* HR enters the three form fields at creation. *Assumption:* storing them as three nullable columns of `employees` (personal email and phone are contact data of the person, employment type is an employment attribute — none is account identity: the login name stays the work email); phone format; employment type independent of `status` (the status enum also has `contract`/`probation`, a documented overlap, E5). *Not modeled:* photo, address, emergency contact, bank, PAN/Aadhaar (no rules, sensitivity, masking or encryption defined) and which fields "Self" may edit. *Default:* keep the three columns; add a separate profile table only when step-6 rules are defined; self-edit limited to profile fields, never employment fields. *Proceed?* The three fields: done (additive, nullable); self-edit and step-6 data: **no**.

**E1 — update concurrency contract. IMPLEMENTED as backward compatible.** Optional `If-Match`/`ETag` (412). *Needs approval only to make it mandatory* (428), which would break any client that does not send it. *Default:* optional now, mandatory once the frontend sends it.

**E8 — role assignment. BLOCKED, nothing implemented.** *Exact blocker:* the approved permission catalog (D3) has no permission for assigning a user's role (`roles.manage_permissions` is about editing a role's permission JSON, not assigning roles to users, and only Super Admin holds it), and the document gives no policy for who may assign which role. Implementing it with an existing permission would either make HR promotion impossible (Super Admin only) or silently invent privilege rules. *Proposed policy for approval:* new catalog permission `roles.assign` held by HR Admin and Super Admin; HR may only move users between `employee` and `manager`; only Super Admin may grant or remove `hr_admin`/`super_admin`; nobody may change their own role; target must be active and in the caller's company; the system roles stay immutable; the last `hr_admin` cannot be demoted; audited `user.role_assigned` with old/new role keys; sessions need no revocation because permissions are read per request. *Depends on:* every manager-only capability (a manager account cannot exist outside seeding today), role management, Phase 2 approvals. *Proceed?* **No.**

**D10 — manager deactivation. UNCHANGED, approval needed.** *Document:* reports "must be reassigned to another manager by HR" (5.2 step 6). *Options:* (a) allow and report the count (current; reports keep pointing at an inactive manager and can still be edited since the 2026-10-26 fix); (b) block until reassigned; (c) require a `reassignTo` in the request and move all reports atomically, audited per report; (d) auto-reassign to the manager's manager (silent organizational change). *Default:* (c) with an explicit "leave unassigned" option. *Proceed?* No — nothing was reassigned or blocked.

**D2 — Super Admin across companies. UNCHANGED, approval needed.** *Document:* Super Admin scope "Entire platform (all companies)"; checkpoint "sees all companies, can modify role permissions"; "Create / manage companies". *Implemented today:* Super Admin = HR rights inside its own company only. *Default:* a separate platform area with explicit company ids. *Proceed?* No (affects authentication, tenant resolution and every scope service).

**D8 — departments. UNCHANGED, not started.** *Document:* 5.3 create/nest/head/bulk-reassign; `GET/POST/PUT/DELETE /api/departments`; "Configure company settings" has no content. *Open:* delete of a department that has employees or children, the shape of bulk reassignment, company settings. *Default:* block delete while employees/children exist (409) plus an audited bulk-reassign endpoint. *Proceed?* The module was out of scope for this task.

**D6 — failed login of an unknown email. UNCHANGED.** *Document:* log a failed attempt after a password mismatch (the user exists); `audit_log.user_id` is required. *Default:* do not persist unknown-email attempts. *Proceed?* Nothing to implement without a schema decision.

**New assumptions introduced by this session (each needs approval or confirmation):** E9 password policy (12+ characters, ≤ 72 bytes, no control characters); E10 invitation lifetime 72 h; E11 change-password keeps the current session and revokes the others, set-password revokes all; E12 an invitation can only be issued while the account has not set a password, under `employees.create`; E13 first-login accounts may use only `/api/auth/me`, change-password and logout; E14 phone format; E15 `employmentType` values `full_time|part_time|contract`.

### 3. Security controls implemented and how they were verified
Token generation: 32 random bytes (256-bit), base64url; stored as SHA-256 hash only (a test compares the stored value with the hash of the delivered token and with the raw token). Expiry: enforced at lookup and again inside the conditional UPDATE (tested at 30 s inside and 30 s past the lifetime, and with a configured 10 minutes). Single use: conditional UPDATE; 6 parallel requests → exactly one 200, one audit row. Revocation: a new invitation revokes the old one; a failed re-issue rolls back and leaves the old one usable. Tenant: lookup, consume, account load and the current-user loader are each bound to the host's company; a token used on another host is refused and not consumed; a repository-level test pins the lowest layer. Account binding: a token sets only its own account's password. Deactivated or already-set-up accounts: refused. Secrecy: the token and password appear in no response, audit row, log line or `ToString()`; weak passwords are refused before the token is touched. Atomicity: with the audit entry failing, the password change and the token consumption roll back (retry works). Sessions: set-password deletes all of the account's sessions, change-password all others; other accounts untouched. Delivery: after the commit only (a test reads the database from another connection at delivery time), never for a rolled-back creation, and a failing channel leaves the committed employee in place. First-login: all permission-protected endpoints and a custom-role account are refused while pending; the allowed set is pinned by a test.

### 4. Concurrency contract review (phase 6)
The `xmin` token, the `UPDATE … WHERE xmin = …` predicate, the 409 translation, the before/after audit values and the snapshot-only migration (empty `Up`/`Down`, generated SQL = history insert only, tested earlier on a database with employees) were re-verified: all employee concurrency tests pass. `xmin` is not a physical column and no column was added. New: the ETag is that same version; `Update` now always writes the employee row (a name-only edit lives on the account row and previously did not move the version).

### 5. Verification
Focused runs while building: onboarding 31→32, first-login 21, profile/ETag 24, migration safety 4, architecture/wiring 12. Full suite twice: **741 tests — 741 passed, 0 failed, 0 skipped** both times (TRX executed 741, NotExecuted 0), up from 648. Mutation checks listed in the CHANGELOG (one survived at HTTP level because of defense in depth and was killed by a repository-level test). One failure of `EmployeeCodeConcurrencyTests.Failing_requests_among_parallel_ones_do_not_leave_gaps` occurred once during a mutation run that could not have affected it; it did not recur in three isolated reruns or two full runs — recorded as a possible rare load flake. *(Investigated in Session 25: root cause not determined, not reproducible, diagnostics added.)* Clean rebuild 0 warnings / 0 errors; `has-pending-model-changes`: none; seven migrations.

### 6. Safety
Only the guarded Testcontainers environment (random database, marker row, guard before every operation); the new migration was applied only to throwaway databases inside the test container. No Neon, native PostgreSQL, `hrms_db` or `hrms_migration_test` access; no `HRMS-Backend` change, Robocopy, commit, push or deploy; no secret read or printed (the tests use fixed fake values only). Departments, attendance, leave, payroll and every other module were not started.

### 7. Known limitations / remaining blockers before Phase 1 is complete
*(Update, Session 25: the outbox/retry, rate limiting and recording of failed password operations listed below now exist; see Session 25 for what remains.)*
- **Cannot deliver invitations** (no provider): employees created today stay inactive-in-practice until D9 delivery exists or HR re-issues once a channel is configured.
- **Role assignment (E8)**, **departments API (D8)**, **role/permission management API**, **D2** are not started.
- Delivery runs synchronously inside the request after the commit (no outbox/retry); a provider outage means "not delivered, re-issue".
- No rate limiting anywhere: `change-password` can be used to guess the current password with a stolen token; `set-password` tokens are 256-bit so guessing is infeasible.
- Failed attempts at change-password and set-password are not audited (avoids audit spam by anonymous callers); successful ones are.
- Unchanged from earlier: database-level audit immutability, proxy IP behind Render, login's non-atomic session write, advisory-lock code allocation is PostgreSQL-specific, the global-count fragility of one older email test.

## Session 25 — 2026-10-21 — Onboarding reliability and authentication hardening

Date note: the machine clock and the new migration's timestamp say 2026-10-21. The headings of Sessions 21–24 carry later dates (2026-10-24 … 2026-10-27); that inconsistency predates this session and was left alone rather than rewritten.

Method: read the code first (issuer, unit of work, post-commit queue, the two password handlers, login, `Program.cs`, `HttpClientInfo`, the test harness, the failing test and its history in the earlier session transcript), then red-first for each change, then mutation checks. Only `HRMS` was touched. No commit, push, synchronization, deployment, Neon/native-PostgreSQL/`hrms_db`/`hrms_migration_test` access, secret or destructive database operation. All database work ran in the guarded Testcontainers instance.

### 1. Defects found and fixed
1. **Delivery had no durable intent or retry** (as reported): a crash or a provider outage after the commit lost the invitation silently. → transactional outbox (section 2).
2. **Credential endpoints were unthrottled** (as reported). → rate limiting (section 3).
3. **Rejected password operations left no trace** (as reported). → `SecurityEvents` (section 4).
4. **Records leaked secrets through `ToString()`** (new finding): `SetPasswordCommand` and `ChangePasswordCommand` are records; their generated `ToString` prints every member, i.e. the setup token, the bearer JWT and both passwords. Not exploitable today (nothing logged them) but one `LogInformation("{Command}", command)` away. Fixed by overriding `ToString`; a reflection test now fails for any record with a password/token/secret/credential/hash-named string member that keeps the generated one.
5. **Configuration bound too early** (new finding, by the new tests): options read from `builder.Configuration` while composing services do not see `WebApplicationFactory`'s in-memory settings, which are added at build time. The test default `Outbox:WorkerEnabled=false` was silently ignored for the first runs (tests passed by accident, with a live background worker). `Outbox`, `RateLimiting` and `ForwardedHeaders` are now bound lazily from the built host's configuration. Production behavior is the same; the lesson is recorded in CLAUDE.md.
6. **Latent order dependency in an existing test** (exposed by the outbox, fixed in the test): `Re_issuing_revokes_the_earlier_invitation...` relied on `ORDER BY CreatedAt` separating two tokens created in the same instant under the frozen test clock; it failed 1 time in 3 once the physical row order changed. The test now advances the clock between the invitations (as real time would). The assertions are unchanged.
7. **Rate-limit rejections were invisible** (found in the final review): each is now logged as a warning with method and path only.
No authorization, tenant-isolation, token-expiry, replay or atomicity defect was found in the existing password flows (section 5).

### 2. Outbox design, threat model and limitations
*Problem:* only the SHA-256 of a setup token is stored, so a durable queue cannot hold "the message to send" without either the raw token or a way to recreate it. *Options:* (A) raw token in the outbox: rejected — read access to one table would be account takeover for 72 hours (the task forbids a raw token in a broadly accessible table). (B) token encrypted with an application key: rejected — introduces a secret that has to exist, be persisted across deployments (ASP.NET Data Protection keys on an ephemeral Render disk would make pending messages undecryptable after a restart) and be rotated; a stolen key plus a database dump is still account takeover. (C) **chosen — mint the token at delivery.** The outbox row records only the intent (user, token id, company). At send time the processor generates the token, replaces the hash of the pending, never-delivered token row (conditional on open + unexpired) and sends it. *Properties:* a database-only attacker obtains nothing usable (placeholder hashes belong to values nobody ever saw); no key management; the promised expiry cannot be extended by a delayed delivery; a consumed, revoked or expired token is never revived (and the message is cancelled); a retry after a failure simply rotates again, so a token that reached a failed channel stops working; the "one usable token per account" invariant holds; no new column holds a secret. *Residual risks:* the raw token exists in process memory and in the mail provider's systems and mailbox (inherent to e-mailing a link); a custom `IAccountInvitationSender` must not log it (the shipped `AccountInvitation.ToString` redacts it); delivery is at-least-once, so a crash between "channel accepted" and "status written" sends a second mail with a newer token — the older link then stops working; a message that waited longer than the lifetime is cancelled rather than sent (HR re-issues).
*Concurrency:* claim by one `UPDATE … FOR UPDATE SKIP LOCKED … RETURNING` with a lease; state changes guarded by (status, attempt) — a worker that lost its lease cannot overwrite the new owner's result. *Failure policy:* defaults 6 attempts, 30 s doubling to at most 1 h (30, 60, 120, 240, 480 s between attempts → terminal at about 15.5 minutes), then `Failed`, logged at error level and never retried; recovery = HR re-issues (new token, new message). *Not built:* retention/purge of finished messages (they hold no secret and no address; purge is a deployment decision), a dead-letter replay UI, per-provider permanent-versus-transient error classification (the sender abstraction has only success or exception), metrics endpoint (logs and the table are the monitoring surface). *What remains for deployment:* a real `IAccountInvitationSender` and the setup-page URL; until then messages wait (no attempt is burned) and are delivered once a channel exists if still inside their lifetime. Tested with a recording/failing double only; no real e-mail was sent.

### 3. Rate limiting (policies and configuration)
See CHANGELOG 2026-10-21 for the numeric defaults. Decisions: (a) one chained global limiter, because a named policy supplies one partition and endpoints need several (address + user + target); (b) keys that cannot be abused against a victim — account budgets include the caller's own address (login) or need a secret (set-password token), per-user budgets need a valid session; (c) the answer is identical for existing and unknown accounts; (d) fixed windows, so every limit ends by itself; (e) one 429 shape with `Retry-After`; (f) in-memory per instance — simple and dependency-free, but a multi-instance deployment multiplies the limits and a restart resets them (a shared store would be needed for strict limits; not built); (g) IPv6 grouped by /64. *Reverse proxy:* forwarded headers are off by default and, when enabled, trusted only from listed proxies/networks (see CLAUDE.md); tested: a header from the real peer is used, entries a client prepended are ignored, a header from an unlisted peer changes nothing even when forwarding is enabled, enabling it without a proxy or with a malformed address stops start-up. *Limits of the design:* users behind one NAT share a per-address budget, so an attacker on the same address can exhaust the address-wide login budget for one window (temporary denial for that address, never for an account elsewhere); without trusted-proxy configuration on Render all users share the proxy's address; the numbers are reasoned defaults, not measured, and need tuning against real traffic.

### 4. Security auditing of rejected password operations
Decision (F3): a new append-only table rather than a nullable `audit_log.user_id`, because changing the audit log's referential guarantee (D6) would affect every audit consumer, whereas a separate table with a nullable user keeps `audit_log` strict. The anonymous invalid-token case therefore never touches `audit_log` (tested: row count unchanged) and never invents an actor. Written on a separate connection so the record survives the command's rollback and a failure to write it cannot change the response or leave a partial password change (both tested). Reasons are fixed codes; tokens, hashes, passwords and submitted e-mails are never stored (tested by dumping the table). Requests rejected by a rate limit never reach the handler, so a flood cannot grow the table faster than the limits allow. Open: unknown-email login attempts are throttled but not stored (D6 unchanged); the table has the same application-level-only append-only guard as the audit log; no retention policy.

### 5. Token lifecycle and replay verification
Covered by existing tests (re-verified green) and new ones: expiry at the boundary; single use under six parallel submissions (one success, one audit row, five losers each producing exactly one event); tenant binding (a foreign host cannot use or burn a token); account binding; deactivated and already-set-up accounts; atomicity (audit failure rolls the password and the consumption back and the token stays usable); set-password does not log anybody in and returns no credential; sessions (all revoked on set-password, others on change-password); first-login enforcement unchanged; the outbox worker cannot revive a consumed/revoked token or extend an expiry; no leak through `ToString`, responses, logs, audit rows, security events, outbox rows or the Swagger document (no example/default secrets; the invitation response schema has no token).

### 6. The intermittent `EmployeeCodeConcurrencyTests.Failing_requests_among_parallel_ones_do_not_leave_gaps`
**Result: root cause not determined. No defect was found in the code allocation path, and the failure could not be reproduced.** What was established:
- *History (from the earlier session's transcript):* it failed once, in the mutation run "M2 expiry not checked", alongside the four expiry tests that mutation is meant to break. That mutation edited only the token repository's expiry conditions, which the employee-creation path does not execute. The same test legitimately failed in the mutation runs that removed the advisory lock and the department check (so it is sensitive to real defects). Mutation runs rebuild and run the whole `HRMS.Tests.Employees` namespace.
- *Code analysis:* a creating request validates the e-mail and department, hashes the placeholder password, takes `pg_advisory_xact_lock(company)`, reads the highest `EMP-n`, inserts and commits in one transaction; a failing request (unknown department) fails before the lock and holds nothing. Under READ COMMITTED each statement gets a fresh snapshot and PostgreSQL makes a commit visible before releasing its locks, so a waiter always sees the previous holder's row; a rolled-back holder writes nothing. There is no lock-order inversion, so no deadlock; no gap and no duplicate can arise by construction.
- *Attempts to reproduce:* 12 isolated repetitions; 8 repetitions of the 8-test class while 40 busy loops saturated all 20 cores (runs up to 89 s); a temporary 80-round stress of exactly this scenario (720 requests, one container; deleted afterwards); about 29 complete runs of the 302-test employee namespace — the context where it failed — as two overlapping loops competing with a 16-mutation run for CPU, Docker and PostgreSQL (3 min 50 s per run instead of 1 min 25 s); 10 repetitions of the class on the final build; 4 full-suite runs. **No occurrence.** Server connection use after the stress: 14 of 100 slots.
- *Hypotheses examined and ruled out:* advisory-lock key collision (only causes waiting); commit/lock-release visibility race; deadlock; connection-pool exhaustion; test-host start-up race (the host is started by the HR token creation before the parallel calls); tenant-domain collision (random 40-bit suffix per test company); thread-pool starvation by the blocking start gate (could stagger requests, cannot change outcomes — replaced anyway); the M2 mutation itself.
- *One observation that points at the environment:* during this session a single run of 90 unrelated tests took 9 min 34 s instead of about 50 s and could not be reproduced in four repetitions; the only failure in it was the order-dependent re-issue test (item 6 above). So the Docker/PostgreSQL host does occasionally stall, which would explain a timeout-shaped one-off, but this is an inference, not a finding.
- *What was changed:* the test reports the status and body of every request and the allocated codes when it fails (so a recurrence names its cause instead of "6 expected, 5 found"); its start gate is asynchronous. Assertions unchanged — the correctness requirement (exactly 6 created, 3 rejected, codes `EMP-0001…EMP-0006` with no gap) is intact. If it ever fails again, capture that message first.

### 7. Migration and API review
Read the generated SQL of all three recent migrations: `EmployeeConcurrencyToken` = the history row only (no `xmin` column — verified again; `xmin` mapping is in the snapshot), `AddAccountSetupTokensAndEmployeeContactFields` = nullable columns + one table + three indexes (unique hash index, `varchar(64)` fits SHA-256 hex, RESTRICT keys, lock timeout), `AddOutboxAndSecurityEvents` = two tables + six indexes (details in CHANGELOG). Observations, no change needed: no database constraint ties a token to being only consumed or revoked, nor requires a 64-character hash (the application is the only writer; a CHECK would be a cheap follow-up); finished token/outbox/security-event/session rows are never purged. ETag/If-Match: strong quoted `xmin`, optional header, `*` and lists handled, weak or malformed tags never match, an unparsable header is 400, mismatch is 412; `xmin` caveats: it is not unique across a 32-bit transaction-id wrap and changes on a dump/restore, which can only cause a refused (412/409) update, never a lost one. CORS exposes `ETag` (tested) but allows only `http://localhost:5173` — the production origin must be configured. Swagger now documents 429 on the four throttled endpoints.

### 8. Verification
Focused: outbox 20, security events 13, rate limiting and proxy policy 27, token lifecycle 10, migration 5, employee-code class repeated. Full suite twice: **816 tests — 816 passed, 0 failed, 0 skipped** both times (TRX executed 816, NotExecuted 0; about 2 min 20 s), up from 741 (75 new). 16 mutation checks, all killed, each proven applied and restored byte-for-byte (list in CHANGELOG). `dotnet build HRMS.slnx --no-incremental`: 0 warnings / 0 errors. `has-pending-model-changes`: none. Eight migrations; the new one applied only to throwaway databases inside the test container. After the work only the pre-existing containers (`hrms-api`, `hrms-postgres`) plus the test reaper remained.

### 9. Decision register additions
**F1 — invitation token at delivery time (chosen option C).** Needs only awareness; revisit if a provider offers templates that need the link at enqueue time. **F2 — retry policy defaults** (6 attempts, 30 s doubling, 1 h cap): reasoned defaults; confirm against the chosen provider's behavior. **F3 — separate `SecurityEvents` table** instead of a nullable `audit_log.user_id`: confirm (D6 stays open for unknown-email logins). **F4 — rate-limit numbers and keys:** reasoned, to be tuned. **F5 — forwarded headers opt-in with an explicit list:** the deployment must supply the platform's proxy addresses/ranges (or consciously choose `TrustAllProxies`). **F6 — retention:** no purge policy exists for outbox, security events, expired tokens or sessions.

### 10. Remaining before this is production-ready (none done)
A real invitation sender and URL (D9); `ForwardedHeaders__*` configuration for the platform proxy; production CORS origin; tuning of `RateLimiting__*`/`Outbox__*`; retention jobs; monitoring of failed/stuck outbox rows; the usual preflights and approvals for migrations (nothing is committed, synchronized, deployed or applied to Neon). Phase 1 still lacks departments (D8), role assignment (E8), role/permission management, Super Admin across companies (D2) and manager reassignment (D10).

## Session 26 — 2026-10-22 — Departments module

Method: extracted every Department passage from the specification, inspected the entity, configuration, permission catalog, system roles, scope/reference services and the employee module's patterns, then implemented only what the document defines. Only `HRMS` was touched; no commit, push, synchronization, deployment or persistent-database access.

### 1. What the specification says (exact)
- 5.3: "HR creates department - Name, parent department (optional - for nested org), department head (an existing employee)." "Each employee record has department_id. HR can bulk-reassign employees between departments." "departments.parent_dept_id allows e.g. Engineering -> Frontend -> Mobile Sub-team with unlimited depth." "departments.head_emp_id is a reference to employees. The head sees department-level reports."
- Table 5: id, company_id (FK), name VARCHAR(100), head_emp_id (FK employees, NULL initially), parent_dept_id (FK self), created_at.
- API table: `GET /departments` All - "List departments (HR: all; Manager: own dept)"; `POST /departments` HR Admin; `PUT /departments/:id` HR Admin - "Update department name / head / parent". Phase 1 checklist: "GET/POST/PUT/DELETE /api/departments - full department CRUD"; frontend: "Department management page (HR admin only): create, edit, nest departments".
- Not stated anywhere: what DELETE does (and for a department with employees or sub-departments), how bulk reassignment works, name uniqueness, list pagination/filtering, who may read a single department, what "the head sees department-level reports" means before Phase 2 reports exist.

### 2. Already implemented before this task
Entity and schema (Table 5), the permission catalog entries (`view_own_department`, `view_all`, `manage`) and their assignment to the system roles (employee and manager: own department; HR Admin and Super Admin: all three), the reference validator and the employee module's use of department ids. No endpoint, scope rule, audit event or test existed.

### 3. Implemented
Create, update, list, get (contract in CHANGELOG 2026-10-22). Authorization reuses the existing filter, `RequirePermission`, `ICurrentUser`, `IDataScopeService` (new department read scope) and `IReferenceValidator`; there is no second mechanism. The company is never read from a request; foreign and missing ids and references answer identically (tested). Hierarchy integrity: cycle check plus a per-company advisory lock (a deterministic test holds the lock from another connection and shows the update waits while another company is unaffected; a parallel test never produces a cycle).

### 4. Blocking decisions (not implemented; need your approval)
**DEL - DELETE and employees in a department.** Wording: the checklist names DELETE; nothing else defines it. Facts: `Employees.DepartmentId` is required with FK RESTRICT, employees are never deleted (5.2), `Departments.ParentDeptId` is RESTRICT, `HeadEmpId` is SET NULL, the company FK cascades. Options: (a) no delete (current: 405); (b) delete only an empty department with no sub-departments, else 409; (c) soft-deactivate (needs a column and migration); (d) delete with forced reassignment. *Recommendation:* (b) - it cannot lose data or move people silently - with an audited `department.deleted`; revisit (c) if history of removed departments is needed. Not presented as approved.
**REA - bulk reassignment.** Wording: "HR can bulk-reassign employees between departments." Open: request shape (list of ids or "all of department X"), whether managers/heads are adjusted, one audit entry per employee or one summary, all-or-nothing. *Recommendation:* `POST /api/departments/{id}/reassign-employees` with `{toDepartmentId, employeeIds[]}`, permission `employees.edit_any`, one transaction, one `employee.department_changed` audit entry per employee, same-company validation of every id. Single-employee moves already work through `PUT /api/employees/{id}`.
**Non-blocking assumptions (confirm or change):** A1 a manager/employee reads only the department of their own employee record (the document's "Manager: own dept" applied also to employees, who hold `view_own_department` in the approved catalog); A2 no name uniqueness; A3 no pagination/filter (flat list ordered by name; the client builds the tree from `parentDepartmentId`); A4 a head must not be inactive; A5 PUT replaces all three fields and has no ETag (concurrent edits of different fields can overwrite each other; an `xmin` token like the employee's would need a snapshot-only migration); A6 `employeeCount` counts every assigned record regardless of status; A7 the parent's name is shown to anyone who may read the child; A8 a no-change PUT still writes an audit entry (as for employees); A9 "the head sees department-level reports" is Phase 2 and gives the head no extra rights now; A10 Super Admin has the same company-only scope as HR (D2 unchanged).

### 5. Security review
Tenant isolation: every repository and query starts from `ICurrentUser.CompanyId` (throws when unresolved); the scope service bounds all reads; references are validated against the caller's company; tested with two companies for read, update, create under a foreign parent, re-parent under a foreign parent, a foreign token on another host, and a spoofed `companyId` in the body. Fail-closed: no permission, view_all alone, manage alone (cannot read), deactivated account, unauthenticated. Remaining: no database CHECK forbids a self-parent and nothing at database level enforces same-company parent/head (the application is the only writer; a composite foreign key or trigger would be a migration); database-level audit immutability is still open.

### 6. Verification
Build 0 warnings / 0 errors; `has-pending-model-changes`: none; no migration; full suite twice **845 passed, 0 failed, 0 skipped** (TRX executed 845, NotExecuted 0); 29 new tests; 9 mutation checks killed. Swagger is asserted to list the four operations with DTO schemas only and no DELETE. Not performed: no manual run against the Swagger UI, no load test, no migration test (there is no migration).

### 7. Unresolved
*(Update, Session 27: DEL and REA were approved and implemented.)* Blockers for the full spec: DEL, REA. Everything else above is an assumption to confirm. Phase 1 still lacks role assignment (E8), role/permission management endpoints, Super Admin across companies (D2), manager reassignment (D10), self-edit/profile (D7), an invitation delivery provider (D9) and the deployment items listed in Session 25.

## Session 27 — 2026-10-22 — Departments: delete and bulk reassignment

Approved decision (from you): delete only when no employees and no child departments, else 409; reassignment via POST {id}/reassign-employees with {toDepartmentId, employeeIds}, using the existing departments permission. Only `HRMS` was touched; no commit, push, deployment, Neon/native PostgreSQL/`hrms_db`/`hrms_migration_test` access; all database work in the guarded Testcontainers instance. The working tree contained only this project's earlier uncommitted work; nothing unrelated was altered.

### 1. Inspection findings
`Employees.DepartmentId` (required) and `Departments.ParentDeptId` are RESTRICT; `Departments.HeadEmpId` is SET NULL and points at employees, not at departments, so deleting a department cannot affect a head; `Companies -> Departments` cascades (irrelevant here, companies are not deleted). Employees have an `xmin` concurrency token; `IEmployeeRepository.SaveChangesAsync` already turns a lost race into 409 and a vanished reference into 409. The audit pipeline accepts several entries for one declaration (each declaration consumes one distinct entry). Permissions: `departments.manage` is held by hr_admin and super_admin only.

### 2. Concurrency analysis (what is and is not guaranteed)
- *Delete vs employee assignment / child creation:* **guaranteed by PostgreSQL**: an uncommitted referencing row holds a key-share lock on the department row, so the DELETE waits and then fails the foreign key (409, rolled back); if the delete commits first, the other insert/update fails its foreign key (the existing employee conflict mapping returns 409). The application's counts only produce the friendly message earlier; verified by removing them.
- *Delete vs reassignment into the department:* same mechanism; a test races them five times and asserts never "both succeeded" and never an employee pointing at a missing department.
- *Two simultaneous bulk reassignments of the same employees:* guaranteed by the employees' `xmin` token (the second UPDATE matches no row -> 409) or, when it reads after the first commit, by the source-membership validation (400). Never a mixture.
- *Reassignment vs a concurrent single-employee edit:* same token (409, whole save rolled back). Preserved, not changed.
- *Delete vs re-parenting:* serialized by the per-company hierarchy lock (also taken by delete).
- *Limits:* isolation is READ COMMITTED, not serializable (nothing here claims otherwise); reassigning does not take a lock on the destination department, so a delete of the destination committing between validation and save is refused by the foreign key (409) rather than prevented; the 500-employee cap and a single transaction mean a large batch holds row locks for its duration; the department hierarchy lock is per company, so heavy hierarchy edits of one company queue.

### 3. Assumptions to confirm
R1 cap of 500 employees per request; R2 offboarded employees may be moved; R3 moving employees does not change their manager, status or head assignments (a moved employee who is the head of the source department stays its head - the head need not belong to the department, as decided in Session 26); R4 one audit entry per employee and no department-level summary entry; R5 (no assumption needed) heads are stored on the department itself, so deleting a department cannot affect any employee's head role; R6 audit `department.deleted` stores name/parent/head ids only.

### 4. Verification
Build 0 warnings / 0 errors (`--no-incremental`); no pending model changes; eight migrations; 864 tests passed twice, 0 failed, 0 skipped (TRX executed 864, NotExecuted 0); 20 new tests; mutation checks as listed in the CHANGELOG. Not done: no manual Swagger-UI session, no load test of a 500-employee batch, no migration test (none exists).

### 5. Remaining decisions
None blocks the Department module. Confirm R1-R4. Optional improvements: an ETag for department updates (A5), a unique-name rule (A2), a database CHECK/trigger against self-parent and cross-company references.

## Session 28 — 2026-10-22 — Roles API (read-only)

Preceded by a read-only readiness review (no files changed): the specification defines only `GET /roles` and `PUT /roles/:id/permissions` (plus a vague "GET/PUT /api/roles" in the checklist); it does not define role assignment, custom-role creation/deletion, or how the first Super Admin is provisioned. Premises of the task were re-checked in code before building: no roles controller/query existed, `roles.view` and `roles.manage_permissions` exist and only `super_admin` holds them, permissions are read from the database on every request, `RolePermissions.Parse` fails closed.

### Implemented
`GET /api/roles` (contract and scope in CHANGELOG 2026-10-22 "Roles API"). Files: `RoleContracts.cs`, `IRoleQueries.cs`, `ListRolesQuery.cs`, `RoleQueries.cs`, `RolesController.cs`, one DI line, `RolesApiTests.cs`.

### Security review of the implementation
No entity is serialized (DTO with an explicit field set, asserted). Tenant: the only filter input is `ICurrentUser.CompanyId` (throws when unresolved); tested that query-string and header company values change nothing, that two companies see their own custom roles and the same system-role ids, and that a token from one company is refused on another's host. Fail-closed: deactivated account, role belonging to another company, pending first-login password, role holding only `manage_permissions`, a role named `super_admin` that holds nothing, and a role with malformed permissions. One query per request (roles only), no per-role lookups.

### Limitations / unresolved
- **D2:** system roles are shared by every company, so the listing of them is the same for any company's Super Admin. Under the chosen read-only design that is harmless; **editing** them would be platform-wide and is NOT built - it needs the D2 decision (platform-level area recommended).
- **E8** (role assignment) and **first Super Admin provisioning**: unchanged. Nobody holds `roles.view` in a production-seeded system until a Super Admin is provisioned (the production seeder creates an `hr_admin`), so the endpoint is correct but currently unusable there.
- The spec's "all roles" is read as "all roles visible to the caller's company", not other companies' roles.
- Pagination/filtering is not offered (a company has a handful of roles).
- Write endpoints, custom-role management, permission-catalog expansion and any migration (for example a concurrency token for roles) are separate tasks.

### Verification
Build 0 warnings / 0 errors (`--no-incremental`); no pending model changes; eight migrations; full suite twice **881 passed, 0 failed, 0 skipped** (TRX executed 881, NotExecuted 0); 17 new tests; 5 mutations killed. Not done: no manual Swagger-UI session.

## Session 29 — 2026-10-22 — Super Admin provisioning

### Findings (verified in code and specification)
- Spec section 2: Super Admin = "Entire platform (all companies). Vendor-level. Creates companies, manages all admins ... Typically 1-2 users only." No provisioning workflow, no company-creation workflow, no platform host rule.
- `User.CompanyId` is required; login resolves the tenant from the host and looks the user up inside that company; `TenantMiddleware` refuses a token whose company differs from the host's. So **a platform identity cannot be represented in the current model**: every user, including a Super Admin, belongs to exactly one company. Representing a platform administrator would need a decision (a designated platform company, a flag or table, and a host/tenant rule) - that is D2, not invented here.
- The production seeder creates a company and an `hr_admin`; nothing creates a `super_admin`. The role has `roles.*` and `system.configure_company` and inherits HR Admin's permissions; HR Admin has no `roles.*` (now pinned by a test).
- Existing secret convention: environment variables read through configuration (`HRMS_ADMIN_PASSWORD`, `HRMS_DEV_ADMIN_PASSWORD`), no CLI or admin tooling exists.

### Options considered
A one-time bootstrap from explicitly configured values at start-up (chosen); B a controlled command/CLI (needs new tooling and an authenticated operator channel that does not exist); C the invitation flow (the outbox cannot deliver: no provider; and it needs a tenant context). A fits the existing seeding pattern with the smallest change. Rejected variants: defaulting the company to `HRMS_TENANT_DOMAIN` (would silently decide D2 by placing the vendor administrator inside the first customer company - the company must be named explicitly); promoting an existing user (silent privilege change); an anonymous or HTTP bootstrap endpoint (never).

### Decision register
**P1 - where the Super Admin lives.** Today: in the explicitly configured company, company-scoped. Recommended long-term (with D2, option B): a dedicated platform company that holds only vendor accounts, so customers' companies never contain a vendor administrator; moving the account later means re-provisioning (no data depends on it yet). **P2 - rule "only while no Super Admin exists"** prevents duplicates and resurrection; the consequence is that if the only Super Admin is locked out, recovery needs an operator step on the database (or a future recovery procedure). **P3 - temporary configured password with first-login enforcement** reuses the existing change-password flow; the secret remains in the environment store until removed. **P4 - the bootstrap event is stored in `SecurityEvents`** (nullable user, append-only) because `audit_log.user_id` must be a real actor (D6); the table's purpose is widened from "rejected attempts" to "security-relevant events without an acting user".

### Risks that remain
The password sits in the platform's environment configuration until removed; the Super Admin cannot yet do anything platform-level; a Super Admin inside a customer company is visible to that company's HR Admin in employee/user data (no employee record is created for it, so it is not listed as an employee, but its user row exists); `Program.cs` start-up wiring is verified by build and by the provisioner's own tests, not by a Production-mode start; startup fails on a bad configuration by design.

### Verification
Build 0 warnings / 0 errors; no pending model changes; eight migrations; full suite twice 903 passed, 0 failed, 0 skipped (TRX executed 903); 22 new tests; 7 mutations killed. No production or persistent database was touched; nothing committed, pushed or deployed.

## Session 30 — 2026-10-23 — Role permission validation and concurrency foundation

Work done in two parts because Docker Desktop was not running when the database tests were first attempted (the engine pipe did not exist; the tests refused to fall back to any other database, as designed). After Docker was started by the user the pre-existing containers restarted on their own; they were not touched. No test result was claimed before Docker was available.

### Premises verified in code
`Role` had no concurrency token; `Employee.Version` uses `IsRowVersion()` over `xmin` with a snapshot-only migration; `RolePermissions.Parse` (read side) silently drops unknown/malformed values; the catalog has four modules; the shipped system roles satisfy employee ⊆ manager ⊆ hr_admin ⊆ super_admin and only `super_admin` holds `roles.*`; `GET /api/roles` exists and no write route does.

### Differences between read and write validation
Read: never throws, drops anything unrecognised, so a bad stored value grants nothing. Write: refuses the whole document with a coded, located reason; no repair, no dropping, ordinal matching, no empty modules, duplicates (including repeated JSON keys) rejected, size/count limits, canonical output.

### Ambiguities and assumptions (need confirmation)
V1 the specification has no limits for the document: 16 KiB / 64 modules / 64 actions are assumptions. V2 an empty module array is rejected rather than normalised (one representation of "no permission"). V3 the example JSON in the specification uses modules (attendance, leave, timesheet, payroll) that are not in the catalog, so such a document is refused until the catalog grows (Phase 2); each new permission will also need a data migration that merges it into existing role rows without overwriting edits (the seeder is insert-only). V4 "role management is Super Admin only" is read from the permission matrix and the API table; it is enforced for the three lower built-in roles as an invariant. V5 the hierarchy is enforced on built-in roles only. V6 an update that changes nothing writes nothing and does not move the version (EF only issues an UPDATE for modified columns), so a future handler should not treat "no change" as a conflict.

### Security review
No endpoint, permission, tenant behavior, role assignment or role row was added or changed (verified: the migration SQL is only the history row; existing read-side authorization tests and the "write routes do not exist" test pass). The new repository is bound to the caller's company and fails closed. The concurrency exception carries a fixed message and no data. Remaining concerns for the endpoint task: editing a built-in role is platform-wide (D2), so who may do it and how it is audited across companies must be decided first; the audit entry would land in the editor's company only; a custom role edited by a Super Admin must not receive permissions the editor lacks (not enforced yet, needs the handler); ETag/If-Match is not wired because there is no response to carry it yet.

### Verification
Build 0 warnings / 0 errors (`--no-incremental`); no pending model changes; nine migrations; full suite twice 983 passed, 0 failed, 0 skipped (TRX executed 983); 80 new tests; 7 mutations killed. Only the guarded Testcontainers databases were used; no Neon, native PostgreSQL, `hrms_db` or `hrms_migration_test` access; nothing committed, pushed or deployed.

## Session 31 — 2026-10-23 — Platform identity foundation

Implements the approved direction from the Session 30/31 review: option C (platform company + explicit `IsPlatform` flag + separate gate). Only `HRMS` was touched; only guarded Testcontainers databases were used; no real platform company or administrator was created or designated; nothing committed, pushed or deployed.

### What exists now
Schema flag and single-platform index; `ICurrentUser.IsPlatform` from the database; `[PlatformOnly]` with filter enforcement, conflict classification, runtime area backstop and convention tests; provisioner restricted to the platform company; operator runbook (docs/PLATFORM_ADMINISTRATION.md).

### Verification of the invariants
HR Admin / Employee / Manager are refused on the gate even inside the platform company; a Super Admin of an ordinary company is refused; a platform-company user without the permission for THAT action is refused (a view-only role passes the view probe and fails the manage probe); a user with the permission outside the platform company is refused; forged role/claim values and caller-supplied company ids, headers and query values change nothing; missing and ambiguous designation fail closed and a change applies on the very next request with the same token; controller/action attributes (`[AllowAnonymous]`, `[SelfService]`, missing permission, forgotten gate) cannot bypass it; deactivated and password-change-pending accounts are refused; the platform administrator keeps company-scoped employee and department access; `GET /api/roles` is unchanged.

### Open points and risks
1. **No platform company exists yet.** Designation is a manual operator step (runbook). The existing production database has one company; whether it becomes the platform company or a second, dedicated company is created first is a deployment decision (P2) - using the sole customer company as the platform makes its Super Admins platform administrators.
2. Existing `super_admin` accounts in an ordinary company (only possible in non-production databases from the earlier bootstrap) keep no platform authority but are not removed; the runbook has the query to find them.
3. A designation is a plain database update; only an operator with database access can move it, and it is traced only if the operator records the `SecurityEvents` row as the runbook says (the application cannot see it happen). A database trigger or a platform endpoint for it are possible later hardening.
4. The gate decides "who may call", not "what a platform operation may do": each platform endpoint still needs its own validation, invariants, audit, concurrency and, for shared roles, the blast-radius considerations in Session 30.
5. MFA, network restriction of the platform host and log/audit retention are planned hardening, not implemented.
6. `Program.cs` Production-mode start-up wiring of the provisioner: covered since the release-readiness check by `ProductionStartupTests` (4 tests, scratch databases; suite now 1029/1029). Supplying `HRMS_SUPERADMIN_*` before an operator designates the platform company stops start-up by design (nothing created); designate first, then restart.

### Verification
Build 0 warnings / 0 errors (`--no-incremental`); `has-pending-model-changes`: none; ten migrations; full suite twice 1025 passed, 0 failed, 0 skipped (TRX executed 1025); 42 new tests; 6 mutations killed.

## Session 32 — 2026-10-23 — Frontend integration preparation

Scope: configurable CORS plus investigations and documentation (host strategy, invitation workflow, trusted proxies, migration safety, integration guide). Only `HRMS` touched; guarded Testcontainers only; nothing committed, synchronized, pushed or deployed.

### Findings
1. Tenant = request hostname only (`TenantMiddleware` -> `TenantResolver`, case-insensitive exact match, ambiguous = unknown). Unchanged; now pinned by a test that a trusted proxy's `X-Forwarded-Host`, a company-id header and another tenant's host cannot switch tenant.
2. Forwarded-header handling already met the requested design (off by default; `X-Forwarded-For/Proto` only; trusted only from named proxies/networks; start-up refusal when enabled without any proxy). Added a test for a CIDR range containing / not containing the peer. Render values are documented as unverified in docs/DEPLOYMENT_NOTES.md.
3. Migrations still apply at start-up. Recommended release procedure documented (backup, preflight, SQL review, rehearsal, deliberate apply, rollback limits). `AddUserEmailNormalizedCheck` aborts with counts only and changes nothing if data violates it. Behaviour not changed.
4. Invitations: no provider; recommendation is the seeded admin for development, manual operator step for other roles, and a proposed (unbuilt) development-only link writer needing approval.

### Verification
Build, `has-pending-model-changes` and the full suite: see the changelog entry for the counts.

---

## Remaining work (as of 2026-10-27)

Immediate (see `PROJECT_STATUS.md` for the exact ordered list):
1. ~~Test AlignPhase1Schema through the EF Core migration runner~~ — done in Session 11 (rollback and reapply on the disposable database).
2. ~~Remove the temporary `pgpass.conf` entry~~ — done and verified (see "Credential cleanup" under Session 11).
3. Decide whether to close the remaining test gaps (a full `Program.Main` start in Production against a disposable database, EF-runner guard/lock scenarios, production-scale rehearsal and Neon role ownership). `HttpClientInfo` normalization is now unit-tested (Session 12).
3a. ~~Enforce bcrypt cost 12+~~ — done in Session 13. ~~Normalize emails on write (defect D3)~~ — done in Session 14 (no test is skipped any more). **Before deploying: run the production email preflight queries in `PROJECT_STATUS.md` §6.**
4. Review and approve before any synchronization to `HRMS-Backend`; nothing has been synchronized, committed or pushed.

Phase 1 functionality still not implemented: change-password and first-login behavior, employee/department/roles APIs, audit-log writing, welcome email. (Permission/role/scope enforcement and the 4 system roles were added in Session 17; the first automated tests exist since Session 12.) Phase 2 has not been started.
