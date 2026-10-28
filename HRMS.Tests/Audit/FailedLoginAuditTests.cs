using System.Net;
using System.Text.Json;
using HRMS.Application.Audit;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HRMS.Tests.Audit;

/// <summary>The one audit event the existing production code supports: a rejected password (specification section 4, step 3).</summary>
[Collection(PostgresCollection.Name)]
public class FailedLoginAuditTests : IntegrationTestBase
{
    public FailedLoginAuditTests(PostgresFixture fx) : base(fx) { }

    private async Task<List<AuditLog>> RowsAsync(Guid companyId)
    {
        await using var db = Fx.CreateDbContext();
        return await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId).OrderBy(a => a.CreatedAt).ToListAsync();
    }

    [Fact]
    public async Task A_wrong_password_returns_401_and_writes_one_audit_row_for_that_account()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        const string submitted = "Definitely-Wrong-Password-9!";

        var response = await ClientFor(tenant.Domain).PostLoginAsync(tenant.Email, submitted);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var row = Assert.Single(await RowsAsync(tenant.CompanyId));
        Assert.Equal(tenant.UserId, row.UserId);
        Assert.Equal(tenant.CompanyId, row.CompanyId);
        Assert.Equal(tenant.UserId, row.TargetId);
        Assert.Equal(AuditModules.Auth, row.Module);
        Assert.Equal(AuditActions.LoginFailed, row.Action);
        Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, row.IpAddress);       // from the trusted client-info abstraction
        Assert.Null(row.OldValue);

        // No credential of any kind reaches the log: not the submitted password, not the stored hash, not the email text.
        var stored = (row.OldValue ?? string.Empty) + (row.NewValue ?? string.Empty) + row.Module + row.Action;
        Assert.DoesNotContain(submitted, stored);
        Assert.DoesNotContain(tenant.Password, stored);
        Assert.DoesNotContain("$2", stored);
        using var document = JsonDocument.Parse(row.NewValue!);
        Assert.Equal("invalid_password", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Every_failed_attempt_is_recorded_separately()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var client = ClientFor(tenant.Domain);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostLoginAsync(tenant.Email, "wrong-password-" + i)).StatusCode);
        }

        Assert.Equal(3, (await RowsAsync(tenant.CompanyId)).Count);
    }

    [Fact]
    public async Task An_unknown_email_returns_401_and_writes_nothing_because_no_valid_user_id_exists()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);

        var response = await ClientFor(tenant.Domain).PostLoginAsync("nobody-" + Guid.NewGuid().ToString("N")[..8] + "@" + tenant.Domain, "whatever-password-1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await RowsAsync(tenant.CompanyId));
    }

    [Fact]
    public async Task A_correct_login_is_not_audited_because_the_specification_does_not_ask_for_it()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);

        await ClientFor(tenant.Domain).LoginAsync(tenant.Email, tenant.Password);

        Assert.Empty(await RowsAsync(tenant.CompanyId));
    }

    [Fact]
    public async Task A_wrong_password_for_a_deactivated_account_is_audited_and_a_right_one_is_not()
    {
        var tenant = await TestData.CreateTenantAsync(Fx, userActive: false);
        var client = ClientFor(tenant.Domain);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostLoginAsync(tenant.Email, "wrong-password-1")).StatusCode);
        Assert.Single(await RowsAsync(tenant.CompanyId));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostLoginAsync(tenant.Email, tenant.Password)).StatusCode);   // deactivated: 403, no new row
        Assert.Single(await RowsAsync(tenant.CompanyId));
    }

    [Fact]
    public async Task Attempts_are_attributed_to_the_tenant_of_the_host_and_never_leak_across_companies()
    {
        var a = await TestData.CreateTenantAsync(Fx);
        var b = await TestData.CreateTenantAsync(Fx);

        // Company A's email on company B's host: unknown there, so nothing is written for either company.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ClientFor(b.Domain).PostLoginAsync(a.Email, a.Password)).StatusCode);
        Assert.Empty(await RowsAsync(a.CompanyId));
        Assert.Empty(await RowsAsync(b.CompanyId));

        Assert.Equal(HttpStatusCode.Unauthorized, (await ClientFor(a.Domain).PostLoginAsync(a.Email, "wrong-password-1")).StatusCode);
        Assert.Single(await RowsAsync(a.CompanyId));
        Assert.Empty(await RowsAsync(b.CompanyId));
    }

    [Fact]
    public async Task A_failing_audit_write_still_returns_401_and_is_logged_as_an_error()
    {
        var tenant = await TestData.CreateTenantAsync(Fx);
        var logs = new CapturingLoggerProvider();
        using var factory = new HrmsWebApplicationFactory(Fx, services =>
        {
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddScoped<IAuditWriter, ThrowingAuditWriter>();
        });
        factory.AssertUsesFixtureDatabase();

        var response = await factory.ClientFor(tenant.Domain).PostLoginAsync(tenant.Email, "wrong-password-1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("could not be written"));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("wrong-password-1"));
        Assert.Empty(await RowsAsync(tenant.CompanyId));
    }

    private sealed class ThrowingAuditWriter : IAuditWriter
    {
        public void Stage(AuditEntry entry) => throw new InvalidOperationException("simulated audit outage");

        public Task RecordFailedLoginAsync(Guid userId, Guid companyId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated audit outage");
    }
}
