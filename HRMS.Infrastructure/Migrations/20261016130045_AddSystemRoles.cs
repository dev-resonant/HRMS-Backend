using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HRMS.Infrastructure.Migrations
{
    /// <summary>
    /// Authorization foundation (decision D4). Seeds the four system roles (employee, manager, hr_admin, super_admin;
    /// CompanyId NULL, IsSystem true) and moves existing company-scoped "Admin" users to hr_admin, keeping every company and
    /// user id. Idempotent and non-destructive: it never deletes or rewrites a company, user or role row (the legacy
    /// company "Admin" role rows are left in place, unused). The permission JSON below must equal
    /// <c>HRMS.Domain.Authorization.SystemRoles</c>; a test keeps the two identical.
    /// </summary>
    public partial class AddSystemRoles : Migration
    {
        private const string EmployeeJson = """{"departments":["view_own_department"],"employees":["edit_own","view_own"]}""";

        private const string ManagerJson = """{"departments":["view_own_department"],"employees":["edit_own","view_own","view_team"]}""";

        private const string HrAdminJson = """{"departments":["manage","view_all","view_own_department"],"employees":["create","deactivate","edit_any","edit_own","view_all","view_own","view_team"],"system":["configure_company"]}""";

        private const string SuperAdminJson = """{"departments":["manage","view_all","view_own_department"],"employees":["create","deactivate","edit_any","edit_own","view_all","view_own","view_team"],"roles":["manage_permissions","view"],"system":["configure_company"]}""";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail fast instead of queueing behind long-running queries (transaction-local, resets at commit).
            migrationBuilder.Sql("SET LOCAL lock_timeout = '15s';");

            // SAFETY GUARD - never overwrite foreign data: a company-less role that already uses a system key but is not a system
            // role is unexpected. Abort (the transaction rolls back) with a count only; nothing is changed.
            migrationBuilder.Sql("""
DO $$
DECLARE
    conflicting integer;
BEGIN
    SELECT count(*) INTO conflicting FROM "Roles"
    WHERE "CompanyId" IS NULL AND NOT "IsSystem" AND "Name" IN ('employee', 'manager', 'hr_admin', 'super_admin');

    IF conflicting > 0 THEN
        RAISE EXCEPTION 'AddSystemRoles: % existing company-less role(s) use a reserved system role name but are not system roles. No data was changed. Resolve them manually, then re-run the migration.', conflicting;
    END IF;
END $$;
""");

            // Insert-only and idempotent: a system role that already exists (matched by its key) is left exactly as it is.
            InsertSystemRole(migrationBuilder, "5a000000-0000-4000-8000-000000000001", "employee", "Employee", EmployeeJson);
            InsertSystemRole(migrationBuilder, "5a000000-0000-4000-8000-000000000002", "manager", "Manager", ManagerJson);
            InsertSystemRole(migrationBuilder, "5a000000-0000-4000-8000-000000000003", "hr_admin", "HR Administrator", HrAdminJson);
            InsertSystemRole(migrationBuilder, "5a000000-0000-4000-8000-000000000004", "super_admin", "Super Administrator", SuperAdminJson);

            // The legacy company administrator ("Admin", created per company by the old seeders) becomes hr_admin. Only users
            // whose role belongs to their own company are touched; user and company ids are unchanged.
            migrationBuilder.Sql("""
UPDATE "Users" u
SET "RoleId" = (SELECT s."Id" FROM "Roles" s WHERE s."CompanyId" IS NULL AND s."Name" = 'hr_admin')
FROM "Roles" legacy
WHERE u."RoleId" = legacy."Id"
  AND legacy."CompanyId" IS NOT NULL
  AND legacy."CompanyId" = u."CompanyId"
  AND legacy."Name" = 'Admin'
  AND legacy."IsSystem";
""");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name_System",
                table: "Roles",
                column: "Name",
                unique: true,
                filter: "\"CompanyId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '15s';");

            migrationBuilder.DropIndex(
                name: "IX_Roles_Name_System",
                table: "Roles");

            // Recreate a company "Admin" role for each company that has hr_admin users and no such role, then move them back.
            migrationBuilder.Sql("""
INSERT INTO "Roles" ("Id", "CompanyId", "Name", "DisplayName", "Permissions", "IsSystem", "CreatedAt")
SELECT gen_random_uuid(), c."CompanyId", 'Admin', 'Administrator', '{}'::jsonb, true, now()
FROM (
    SELECT DISTINCT u."CompanyId"
    FROM "Users" u
    JOIN "Roles" r ON r."Id" = u."RoleId"
    WHERE r."CompanyId" IS NULL AND r."Name" = 'hr_admin'
) c
WHERE NOT EXISTS (SELECT 1 FROM "Roles" a WHERE a."CompanyId" = c."CompanyId" AND a."Name" = 'Admin');

UPDATE "Users" u
SET "RoleId" = (SELECT a."Id" FROM "Roles" a WHERE a."CompanyId" = u."CompanyId" AND a."Name" = 'Admin' ORDER BY a."CreatedAt" LIMIT 1)
FROM "Roles" s
WHERE u."RoleId" = s."Id" AND s."CompanyId" IS NULL AND s."Name" = 'hr_admin';
""");

            // Remove the system roles only if nothing references them; otherwise abort rather than orphan or delete users.
            migrationBuilder.Sql("""
DO $$
DECLARE
    still_used integer;
BEGIN
    SELECT count(*) INTO still_used FROM "Users" u JOIN "Roles" r ON r."Id" = u."RoleId"
    WHERE r."CompanyId" IS NULL AND r."Name" IN ('employee', 'manager', 'hr_admin', 'super_admin');

    IF still_used > 0 THEN
        RAISE EXCEPTION 'AddSystemRoles (Down): % user(s) still use a system role. No data was changed. Reassign them first.', still_used;
    END IF;

    DELETE FROM "Roles" WHERE "CompanyId" IS NULL AND "IsSystem" AND "Name" IN ('employee', 'manager', 'hr_admin', 'super_admin');
END $$;
""");
        }

        private static void InsertSystemRole(MigrationBuilder migrationBuilder, string id, string key, string displayName, string json)
        {
            migrationBuilder.Sql($"""
INSERT INTO "Roles" ("Id", "CompanyId", "Name", "DisplayName", "Permissions", "IsSystem", "CreatedAt")
SELECT '{id}'::uuid, NULL, '{key}', '{displayName}', '{json}'::jsonb, true, now()
WHERE NOT EXISTS (SELECT 1 FROM "Roles" WHERE "CompanyId" IS NULL AND "Name" = '{key}');
""");
        }
    }
}
