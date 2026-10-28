using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HRMS.Tests.Persistence;

/// <summary>
/// The fixture applied every migration (including AlignPhase1Schema, with its empty-Employees guard) to a brand-new
/// database in the disposable container. EF Core 9+ also throws during Migrate when the model has pending changes,
/// so a successful fixture start additionally proves the snapshot matches the model.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MigrationsOnEmptyDatabaseTests
{
    private readonly PostgresFixture _fx;

    public MigrationsOnEmptyDatabaseTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task All_ten_migrations_are_applied_and_none_are_pending()
    {
        await _fx.VerifyTargetAsync();
        await using var db = _fx.CreateDbContext();

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

        Assert.Equal(new[]
        {
            "20260621165745_InitialCreate",
            "20260831122714_UpdateHRMSModel",
            "20261012114231_AlignPhase1Schema",
            "20261014112003_AddUserEmailNormalizedCheck",
            "20261016130045_AddSystemRoles",
            "20261021122048_EmployeeConcurrencyToken",
            "20261021130210_AddAccountSetupTokensAndEmployeeContactFields",
            "20261021143131_AddOutboxAndSecurityEvents",
            "20261023075625_RoleConcurrencyToken",
            "20261023105109_AddCompanyIsPlatform"
        }, applied);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task Key_schema_items_from_AlignPhase1Schema_exist()
    {
        await _fx.VerifyTargetAsync();

        var columns = await QueryAsync(
            "select table_name || '.' || column_name || '|' || data_type || '|' || is_nullable || '|' || coalesce(column_default, '-') " +
            "from information_schema.columns where table_schema = 'public'");

        Assert.Contains("Users.IsFirstLogin|boolean|NO|false", columns);
        Assert.Contains("Employees.JoiningDate|date|NO|-", columns);                  // NOT NULL, no permanent default
        Assert.Contains("Employees.LastWorkingDate|date|YES|-", columns);
        Assert.Contains("Roles.Permissions|jsonb|NO|-", columns);
        Assert.Contains("AuditLogs.OldValue|jsonb|YES|-", columns);
        Assert.Contains("AuditLogs.NewValue|jsonb|YES|-", columns);

        var indexes = await QueryAsync("select indexname from pg_indexes where schemaname = 'public'");
        foreach (var expected in new[]
        {
            "IX_Sessions_TokenHash", "IX_Users_CompanyId_RoleId", "IX_Employees_CompanyId_EmpCode",
            "IX_Employees_CompanyId_Status", "IX_AuditLogs_CompanyId_CreatedAt"
        })
        {
            Assert.Contains(expected, indexes);
        }

        var checks = await QueryAsync(
            "select conname || '|' || pg_get_constraintdef(oid) from pg_constraint where conrelid = '\"Users\"'::regclass and contype = 'c'");
        Assert.Contains(checks, c => c.StartsWith("CK_Users_Email_Normalized|CHECK", StringComparison.Ordinal));

        var auditForeignKeys = await QueryAsync(
            "select conname || '|' || confdeltype::text from pg_constraint where contype = 'f' and conrelid = '\"AuditLogs\"'::regclass");
        Assert.Contains("FK_AuditLogs_Companies_CompanyId|r", auditForeignKeys);        // 'r' = RESTRICT
        Assert.Contains("FK_AuditLogs_Users_UserId|r", auditForeignKeys);
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        _fx.VerifyTarget();
        await using var connection = new NpgsqlConnection(_fx.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
