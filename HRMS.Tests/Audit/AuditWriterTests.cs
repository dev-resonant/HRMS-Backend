using System.Text.Json;
using HRMS.Application.Audit;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Audit;
using HRMS.Infrastructure.Common;
using HRMS.Infrastructure.Persistence;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Tests.Audit;

/// <summary>AuditWriter against the disposable PostgreSQL: attribution, JSON, IP, atomicity, independence and append-only.</summary>
[Collection(PostgresCollection.Name)]
public class AuditWriterTests
{
    private readonly PostgresFixture _fx;

    public AuditWriterTests(PostgresFixture fx) => _fx = fx;

    private sealed class FakeCurrentUser : ICurrentUser
    {
        private readonly Guid? _userId;
        private readonly Guid? _companyId;

        public FakeCurrentUser(Guid? userId, Guid? companyId)
        {
            _userId = userId;
            _companyId = companyId;
        }

        public bool IsResolved => _userId is not null;

        public Guid UserId => _userId ?? throw new UnauthorizedAccessException("The current user could not be determined.");

        public Guid CompanyId => _companyId ?? throw new UnauthorizedAccessException("The current user could not be determined.");

        public string RoleKey => "hr_admin";

        public string RoleDisplayName => "HR Administrator";

        public Guid? EmployeeId => null;

        public bool PasswordChangeRequired => false;

        public bool IsPlatform => false;

        public IReadOnlyDictionary<string, string[]> Permissions => RolePermissions.Empty;

        public bool HasPermission(string module, string action) => false;
    }

    private sealed class FakeClientInfo : IClientInfo
    {
        public FakeClientInfo(string? ip) => IpAddress = ip;

        public string? IpAddress { get; }

        public string? UserAgent => null;
    }

    private sealed record Account(Guid CompanyId, Guid UserId, string Domain);

    private async Task<Account> CreateAccountAsync()
    {
        await _fx.VerifyTargetAsync();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var company = new Company { Id = Guid.NewGuid(), Name = $"Audit Co {suffix}", Domain = $"au-{suffix}.test", LogoUrl = string.Empty, Timezone = "Asia/Kolkata", IsActive = true };
        var user = new User
        {
            Id = Guid.NewGuid(), CompanyId = company.Id, RoleId = SystemRoles.HrAdminRoleId, Name = "Audited",
            Email = $"a-{suffix}@{company.Domain}", PasswordHash = "not-a-real-hash", IsActive = true
        };

        await using var db = _fx.CreateDbContext();
        db.Companies.Add(company);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return new Account(company.Id, user.Id, company.Domain);
    }

    private AuditWriter Writer(HrmsDbContext db, ICurrentUser user, Guid? tenantCompany, string? ip = "198.51.100.9")
    {
        var tenant = new TenantContext();

        if (tenantCompany is { } company)
        {
            tenant.SetTenant(company, "t.test");
        }

        return new AuditWriter(db, DbOptions(), user, tenant, new FakeClientInfo(ip));
    }

    private DbContextOptions<HrmsDbContext> DbOptions()
    {
        _fx.VerifyTarget();
        return new DbContextOptionsBuilder<HrmsDbContext>().UseNpgsql(_fx.ConnectionString).Options;
    }

    private async Task<List<AuditLog>> RowsAsync(Guid companyId)
    {
        await using var db = _fx.CreateDbContext();
        return await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId).OrderBy(a => a.CreatedAt).ToListAsync();
    }

    // ---------- attribution and content ----------

    [Fact]
    public async Task A_staged_entry_is_persisted_with_actor_and_company_from_the_current_user_and_the_trusted_ip()
    {
        var account = await CreateAccountAsync();
        var target = Guid.NewGuid();
        var before = DateTime.UtcNow.AddSeconds(-2);

        await using (var db = _fx.CreateDbContext())
        {
            var writer = Writer(db, new FakeCurrentUser(account.UserId, account.CompanyId), account.CompanyId, ip: "198.51.100.9");

            writer.Stage(new AuditEntry("employee", "employee.updated", target,
                oldValue: new { Designation = "Engineer", PasswordHash = "leak" },
                newValue: new { Designation = "Senior Engineer", Token = "leak" }));

            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await RowsAsync(account.CompanyId));
        Assert.Equal(account.UserId, row.UserId);
        Assert.Equal(account.CompanyId, row.CompanyId);
        Assert.Equal("employee", row.Module);
        Assert.Equal("employee.updated", row.Action);
        Assert.Equal(target, row.TargetId);
        Assert.Equal("198.51.100.9", row.IpAddress);
        Assert.InRange(row.CreatedAt, before, DateTime.UtcNow.AddSeconds(2));

        using var oldDoc = JsonDocument.Parse(row.OldValue!);
        using var newDoc = JsonDocument.Parse(row.NewValue!);
        Assert.Equal("Engineer", oldDoc.RootElement.GetProperty("designation").GetString());
        Assert.Equal("Senior Engineer", newDoc.RootElement.GetProperty("designation").GetString());
        Assert.DoesNotContain("leak", row.OldValue! + row.NewValue!);
    }

    [Fact]
    public async Task The_stored_values_are_real_jsonb_and_queryable()
    {
        var account = await CreateAccountAsync();

        await using (var db = _fx.CreateDbContext())
        {
            Writer(db, new FakeCurrentUser(account.UserId, account.CompanyId), account.CompanyId)
                .Stage(new AuditEntry("employee", "employee.created", Guid.NewGuid(), newValue: new { Name = "Asha", Level = 3 }));
            await db.SaveChangesAsync();
        }

        await using var scratch = _fx.CreateDbContext();
        var level = await scratch.Database
            .SqlQuery<int>($"select (\"NewValue\"->>'level')::int as \"Value\" from \"AuditLogs\" where \"CompanyId\" = {account.CompanyId}")
            .SingleAsync();

        Assert.Equal(3, level);
    }

    [Fact]
    public async Task A_system_action_may_have_no_target_and_no_values()
    {
        var account = await CreateAccountAsync();

        await using (var db = _fx.CreateDbContext())
        {
            Writer(db, new FakeCurrentUser(account.UserId, account.CompanyId), account.CompanyId).Stage(new AuditEntry("system", "system.maintenance_run"));
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await RowsAsync(account.CompanyId));
        Assert.Null(row.TargetId);
        Assert.Null(row.OldValue);
        Assert.Null(row.NewValue);
    }

    [Fact]
    public async Task Company_comes_from_the_current_user_not_from_anything_the_entry_could_say()
    {
        var a = await CreateAccountAsync();
        var b = await CreateAccountAsync();

        await using (var db = _fx.CreateDbContext())
        {
            Writer(db, new FakeCurrentUser(a.UserId, a.CompanyId), a.CompanyId).Stage(new AuditEntry("employee", "employee.created", Guid.NewGuid(), newValue: new { CompanyId = b.CompanyId, UserId = b.UserId }));
            await db.SaveChangesAsync();
        }

        Assert.Single(await RowsAsync(a.CompanyId));
        Assert.Empty(await RowsAsync(b.CompanyId));
    }

    // ---------- fail closed ----------

    [Fact]
    public async Task Staging_without_a_resolved_current_user_fails_closed_and_adds_nothing()
    {
        var account = await CreateAccountAsync();

        await using var db = _fx.CreateDbContext();
        var writer = Writer(db, new FakeCurrentUser(null, null), account.CompanyId);

        Assert.Throws<UnauthorizedAccessException>(() => writer.Stage(new AuditEntry("employee", "employee.created")));
        Assert.Empty(db.ChangeTracker.Entries<AuditLog>());
        Assert.Empty(await RowsAsync(account.CompanyId));
    }

    // ---------- IP handling ----------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:85a3:8d3:1319:8a2e:370:7348", "2001:db8:85a3:8d3:1319:8a2e:370:7348")]
    public async Task Missing_or_empty_client_ip_is_stored_as_null_and_real_ips_as_given(string? ip, string? expected)
    {
        var account = await CreateAccountAsync();

        await using (var db = _fx.CreateDbContext())
        {
            Writer(db, new FakeCurrentUser(account.UserId, account.CompanyId), account.CompanyId, ip).Stage(new AuditEntry("employee", "employee.created"));
            await db.SaveChangesAsync();
        }

        Assert.Equal(expected, Assert.Single(await RowsAsync(account.CompanyId)).IpAddress);
    }

    [Fact]
    public async Task An_ip_longer_than_the_column_is_dropped_instead_of_failing_the_business_save()
    {
        var account = await CreateAccountAsync();

        await using (var db = _fx.CreateDbContext())
        {
            Writer(db, new FakeCurrentUser(account.UserId, account.CompanyId), account.CompanyId, new string('9', 46)).Stage(new AuditEntry("employee", "employee.created"));
            await db.SaveChangesAsync();
        }

        Assert.Null(Assert.Single(await RowsAsync(account.CompanyId)).IpAddress);
    }

    // ---------- transaction boundaries ----------

    [Fact]
    public async Task Audit_and_business_change_commit_together()
    {
        var account = await CreateAccountAsync();
        var newName = "Renamed " + Guid.NewGuid().ToString("N")[..6];

        await using (var db = _fx.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var company = await db.Companies.SingleAsync(c => c.Id == account.CompanyId);
            var oldName = company.Name;
            company.Name = newName;
            Writer(db, new FakeCurrentUser(account.UserId, account.CompanyId), account.CompanyId)
                .Stage(new AuditEntry("company", "company.updated", company.Id, new { Name = oldName }, new { Name = newName }));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using var verify = _fx.CreateDbContext();
        Assert.Equal(newName, (await verify.Companies.AsNoTracking().SingleAsync(c => c.Id == account.CompanyId)).Name);
        Assert.Single(await RowsAsync(account.CompanyId));
    }

    [Fact]
    public async Task A_rolled_back_business_change_leaves_no_audit_row_and_no_change()
    {
        var account = await CreateAccountAsync();
        string originalName;

        await using (var db = _fx.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var company = await db.Companies.SingleAsync(c => c.Id == account.CompanyId);
            originalName = company.Name;
            company.Name = "SHOULD NOT SURVIVE";
            Writer(db, new FakeCurrentUser(account.UserId, account.CompanyId), account.CompanyId)
                .Stage(new AuditEntry("company", "company.updated", company.Id, new { Name = originalName }, new { Name = company.Name }));
            await db.SaveChangesAsync();                       // both are inside the open transaction here...
            await transaction.RollbackAsync();                 // ...and both disappear
        }

        await using var verify = _fx.CreateDbContext();
        Assert.Equal(originalName, (await verify.Companies.AsNoTracking().SingleAsync(c => c.Id == account.CompanyId)).Name);
        Assert.Empty(await RowsAsync(account.CompanyId));
    }

    [Fact]
    public async Task A_failing_business_save_takes_the_staged_audit_entry_with_it()
    {
        var account = await CreateAccountAsync();

        await using (var db = _fx.CreateDbContext())
        {
            // Violates the unique (CompanyId, Email) index on the second user, in the same SaveChanges as the audit entry.
            db.Users.Add(new User { Id = Guid.NewGuid(), CompanyId = account.CompanyId, RoleId = SystemRoles.EmployeeRoleId, Name = "Dup", Email = $"dup@{account.Domain}", PasswordHash = "x", IsActive = true });
            db.Users.Add(new User { Id = Guid.NewGuid(), CompanyId = account.CompanyId, RoleId = SystemRoles.EmployeeRoleId, Name = "Dup2", Email = $"dup@{account.Domain}", PasswordHash = "x", IsActive = true });
            Writer(db, new FakeCurrentUser(account.UserId, account.CompanyId), account.CompanyId)
                .Stage(new AuditEntry("employee", "employee.created", Guid.NewGuid()));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        Assert.Empty(await RowsAsync(account.CompanyId));
        await using var verify = _fx.CreateDbContext();
        Assert.Equal(0, await verify.Users.CountAsync(u => u.CompanyId == account.CompanyId && u.Email == $"dup@{account.Domain}"));
    }

    [Fact]
    public async Task A_staged_entry_that_is_never_saved_is_never_persisted()
    {
        var account = await CreateAccountAsync();

        await using (var db = _fx.CreateDbContext())
        {
            Writer(db, new FakeCurrentUser(account.UserId, account.CompanyId), account.CompanyId).Stage(new AuditEntry("employee", "employee.created"));
        }

        Assert.Empty(await RowsAsync(account.CompanyId));
    }

    // ---------- failed login (independent write) ----------

    [Fact]
    public async Task A_failed_login_is_recorded_for_the_existing_account_with_reason_and_ip_and_no_credentials()
    {
        var account = await CreateAccountAsync();

        await using (var db = _fx.CreateDbContext())
        {
            await Writer(db, new FakeCurrentUser(null, null), account.CompanyId, "203.0.113.50").RecordFailedLoginAsync(account.UserId, account.CompanyId);
        }

        var row = Assert.Single(await RowsAsync(account.CompanyId));
        Assert.Equal(account.UserId, row.UserId);
        Assert.Equal(account.UserId, row.TargetId);
        Assert.Equal("auth", row.Module);
        Assert.Equal("auth.login_failed", row.Action);
        Assert.Equal("203.0.113.50", row.IpAddress);
        Assert.Null(row.OldValue);
        using var document = JsonDocument.Parse(row.NewValue!);
        Assert.Equal("invalid_password", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_failed_login_survives_a_rollback_of_the_callers_own_transaction()
    {
        var account = await CreateAccountAsync();

        await using (var db = _fx.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await Writer(db, new FakeCurrentUser(null, null), account.CompanyId).RecordFailedLoginAsync(account.UserId, account.CompanyId);
            await transaction.RollbackAsync();
        }

        Assert.Single(await RowsAsync(account.CompanyId));
    }

    [Fact]
    public async Task A_failed_login_does_not_save_other_pending_changes_of_the_callers_context()
    {
        var account = await CreateAccountAsync();

        await using (var db = _fx.CreateDbContext())
        {
            var company = await db.Companies.SingleAsync(c => c.Id == account.CompanyId);
            company.Name = "PENDING, NEVER SAVED";
            await Writer(db, new FakeCurrentUser(null, null), account.CompanyId).RecordFailedLoginAsync(account.UserId, account.CompanyId);
        }

        await using var verify = _fx.CreateDbContext();
        Assert.NotEqual("PENDING, NEVER SAVED", (await verify.Companies.AsNoTracking().SingleAsync(c => c.Id == account.CompanyId)).Name);
        Assert.Single(await RowsAsync(account.CompanyId));
    }

    [Fact]
    public async Task A_failed_login_is_refused_outside_the_resolved_tenant_or_without_one()
    {
        var a = await CreateAccountAsync();
        var b = await CreateAccountAsync();

        await using var db = _fx.CreateDbContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Writer(db, new FakeCurrentUser(null, null), a.CompanyId).RecordFailedLoginAsync(b.UserId, b.CompanyId));      // account of another company
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Writer(db, new FakeCurrentUser(null, null), tenantCompany: null).RecordFailedLoginAsync(a.UserId, a.CompanyId));   // no tenant
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Writer(db, new FakeCurrentUser(null, null), a.CompanyId).RecordFailedLoginAsync(Guid.Empty, a.CompanyId));

        Assert.Empty(await RowsAsync(a.CompanyId));
        Assert.Empty(await RowsAsync(b.CompanyId));
    }

    [Fact]
    public async Task A_failed_login_for_a_user_that_does_not_exist_cannot_be_stored_and_writes_nothing()
    {
        var account = await CreateAccountAsync();

        await using var db = _fx.CreateDbContext();

        // audit_log.user_id is a required foreign key (specification Table 7), so there is nothing valid to store.
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            Writer(db, new FakeCurrentUser(null, null), account.CompanyId).RecordFailedLoginAsync(Guid.NewGuid(), account.CompanyId));

        Assert.Empty(await RowsAsync(account.CompanyId));
    }

    // ---------- append-only ----------

    private async Task<(Account Account, Guid RowId)> OneRowAsync()
    {
        var account = await CreateAccountAsync();

        await using var db = _fx.CreateDbContext();
        Writer(db, new FakeCurrentUser(account.UserId, account.CompanyId), account.CompanyId).Stage(new AuditEntry("employee", "employee.created", Guid.NewGuid(), newValue: new { Name = "original" }));
        await db.SaveChangesAsync();

        return (account, (await RowsAsync(account.CompanyId)).Single().Id);
    }

    [Fact]
    public async Task Updating_a_tracked_audit_entry_is_refused_and_the_row_is_unchanged()
    {
        var (account, rowId) = await OneRowAsync();

        await using (var db = _fx.CreateDbContext())
        {
            var row = await db.AuditLogs.SingleAsync(a => a.Id == rowId);
            row.Action = "employee.tampered";
            row.NewValue = "{\"name\":\"changed\"}";

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        }

        var stored = Assert.Single(await RowsAsync(account.CompanyId));
        Assert.Equal("employee.created", stored.Action);
        Assert.Contains("original", stored.NewValue);
    }

    [Fact]
    public async Task Deleting_a_tracked_audit_entry_is_refused_and_the_row_remains()
    {
        var (account, rowId) = await OneRowAsync();

        await using (var db = _fx.CreateDbContext())
        {
            db.AuditLogs.Remove(await db.AuditLogs.SingleAsync(a => a.Id == rowId));

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        Assert.Single(await RowsAsync(account.CompanyId));
    }

    [Fact]
    public async Task A_refused_audit_change_does_not_let_unrelated_changes_in_the_same_save_through()
    {
        var (account, rowId) = await OneRowAsync();

        await using (var db = _fx.CreateDbContext())
        {
            (await db.Companies.SingleAsync(c => c.Id == account.CompanyId)).Name = "ALSO NOT SAVED";
            (await db.AuditLogs.SingleAsync(a => a.Id == rowId)).Module = "tampered";

            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using var verify = _fx.CreateDbContext();
        Assert.NotEqual("ALSO NOT SAVED", (await verify.Companies.AsNoTracking().SingleAsync(c => c.Id == account.CompanyId)).Name);
    }

    [Fact]
    public async Task Adding_new_audit_entries_and_ordinary_saves_still_work_after_a_refusal()
    {
        var (account, rowId) = await OneRowAsync();

        await using var db = _fx.CreateDbContext();
        (await db.AuditLogs.SingleAsync(a => a.Id == rowId)).Module = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        Writer(db, new FakeCurrentUser(account.UserId, account.CompanyId), account.CompanyId).Stage(new AuditEntry("employee", "employee.updated", Guid.NewGuid()));
        await db.SaveChangesAsync();

        Assert.Equal(2, (await RowsAsync(account.CompanyId)).Count);
    }
}
