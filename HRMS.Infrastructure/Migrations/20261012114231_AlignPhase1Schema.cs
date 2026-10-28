using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HRMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AlignPhase1Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // JoiningDate is added as NOT NULL with no database default, which is only valid while Employees is empty
            // (verified empty in production). Take the strongest table lock first and keep it until this migration's
            // transaction commits, so no concurrent insert can slip in between the check and ADD COLUMN.
            // lock_timeout (transaction-local) makes the migration fail fast instead of queueing behind long-running
            // queries and stalling every other request on the table behind it.
            migrationBuilder.Sql("SET LOCAL lock_timeout = '15s';");

            migrationBuilder.Sql("LOCK TABLE \"Employees\" IN ACCESS EXCLUSIVE MODE;");

            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""Employees"") THEN
        RAISE EXCEPTION 'AlignPhase1Schema: Employees is not empty; JoiningDate cannot be added as NOT NULL without a backfill.';
    END IF;
END $$;");

            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogs_Companies_CompanyId",
                table: "AuditLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogs_Users_UserId",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_Employees_CompanyId",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_CompanyId",
                table: "AuditLogs");

            migrationBuilder.AddColumn<bool>(
                name: "IsFirstLogin",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "IpAddress",
                table: "Sessions",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            // text -> jsonb has no automatic cast in PostgreSQL, so an explicit USING is required.
            // Fails (and rolls back the migration transaction) if any existing value is not valid JSON.
            migrationBuilder.Sql("ALTER TABLE \"Roles\" ALTER COLUMN \"Permissions\" TYPE jsonb USING \"Permissions\"::jsonb;");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Roles",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "DisplayName",
                table: "Roles",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "EmpCode",
                table: "Employees",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Designation",
                table: "Employees",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            // The Employees lock and empty check are at the start of Up(); the lock is held until commit.
            migrationBuilder.AddColumn<DateOnly>(
                name: "JoiningDate",
                table: "Employees",
                type: "date",
                nullable: false,
                defaultValueSql: "CURRENT_DATE");

            // The default above only satisfies the ADD COLUMN; remove it so inserts must supply a real date.
            migrationBuilder.Sql("ALTER TABLE \"Employees\" ALTER COLUMN \"JoiningDate\" DROP DEFAULT;");

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastWorkingDate",
                table: "Employees",
                type: "date",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Departments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "LogoUrl",
                table: "Companies",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.Sql("ALTER TABLE \"AuditLogs\" ALTER COLUMN \"OldValue\" TYPE jsonb USING \"OldValue\"::jsonb;");

            migrationBuilder.Sql("ALTER TABLE \"AuditLogs\" ALTER COLUMN \"NewValue\" TYPE jsonb USING \"NewValue\"::jsonb;");

            migrationBuilder.AlterColumn<string>(
                name: "Module",
                table: "AuditLogs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "IpAddress",
                table: "AuditLogs",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Action",
                table: "AuditLogs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "IX_Users_CompanyId_RoleId",
                table: "Users",
                columns: new[] { "CompanyId", "RoleId" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TokenHash",
                table: "Sessions",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_CompanyId_EmpCode",
                table: "Employees",
                columns: new[] { "CompanyId", "EmpCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_CompanyId_Status",
                table: "Employees",
                columns: new[] { "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CompanyId_CreatedAt",
                table: "AuditLogs",
                columns: new[] { "CompanyId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogs_Companies_CompanyId",
                table: "AuditLogs",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogs_Users_UserId",
                table: "AuditLogs",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogs_Companies_CompanyId",
                table: "AuditLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogs_Users_UserId",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_Users_CompanyId_RoleId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_TokenHash",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Employees_CompanyId_EmpCode",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_Employees_CompanyId_Status",
                table: "Employees");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_CompanyId_CreatedAt",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "IsFirstLogin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "JoiningDate",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "LastWorkingDate",
                table: "Employees");

            migrationBuilder.AlterColumn<string>(
                name: "IpAddress",
                table: "Sessions",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(45)",
                oldMaxLength: 45,
                oldNullable: true);

            // jsonb -> text with an explicit cast (nullability is unchanged by ALTER COLUMN ... TYPE).
            migrationBuilder.Sql("ALTER TABLE \"Roles\" ALTER COLUMN \"Permissions\" TYPE text USING \"Permissions\"::text;");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Roles",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "DisplayName",
                table: "Roles",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "EmpCode",
                table: "Employees",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "Designation",
                table: "Employees",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Departments",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "LogoUrl",
                table: "Companies",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.Sql("ALTER TABLE \"AuditLogs\" ALTER COLUMN \"OldValue\" TYPE text USING \"OldValue\"::text;");

            migrationBuilder.Sql("ALTER TABLE \"AuditLogs\" ALTER COLUMN \"NewValue\" TYPE text USING \"NewValue\"::text;");

            migrationBuilder.AlterColumn<string>(
                name: "Module",
                table: "AuditLogs",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "IpAddress",
                table: "AuditLogs",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(45)",
                oldMaxLength: 45,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Action",
                table: "AuditLogs",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_CompanyId",
                table: "Employees",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CompanyId",
                table: "AuditLogs",
                column: "CompanyId");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogs_Companies_CompanyId",
                table: "AuditLogs",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogs_Users_UserId",
                table: "AuditLogs",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
