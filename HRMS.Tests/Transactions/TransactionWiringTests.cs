using System.Net;
using FluentValidation;
using HRMS.Application.Interfaces;
using HRMS.Infrastructure.Audit;
using HRMS.Infrastructure.Authorization;
using HRMS.Infrastructure.Common;
using HRMS.Infrastructure.Persistence;
using HRMS.Tests.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HRMS.Tests.Transactions;

/// <summary>The same pattern through the REAL application wiring in Program.cs (pipeline order, DI registrations).</summary>
[Collection(PostgresCollection.Name)]
public class TransactionWiringTests : IDisposable
{
    private readonly PostgresFixture _fx;
    private readonly TransactionCounter _counter = new();
    private readonly TxProbe _probe = new();
    private readonly HrmsWebApplicationFactory _factory;

    public TransactionWiringTests(PostgresFixture fx)
    {
        _fx = fx;
        _factory = new HrmsWebApplicationFactory(fx, services =>
        {
            services.AddSingleton(_counter);
            services.AddSingleton(_probe);
            services.ConfigureDbContext<HrmsDbContext>(o => o.AddInterceptors(_counter));

            // Test-only handlers and a validator, added to the application's own MediatR pipeline.
            services.AddTransient<IRequestHandler<TxCommand, string>, TxHandler>();
            services.AddTransient<IRequestHandler<InvalidTxCommand, string>, InvalidTxHandler>();
            services.AddTransient<IValidator<InvalidTxCommand>, InvalidTxCommandValidator>();
        });
        _factory.AssertUsesFixtureDatabase();
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>A request scope with the tenant resolved and the real current-user service loaded, as the authorization filter would.</summary>
    private async Task<IServiceScope> ScopeAsync(TxAccount account)
    {
        var scope = _factory.Services.CreateScope();
        ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).SetTenant(account.CompanyId, account.Domain);
        var result = await scope.ServiceProvider.GetRequiredService<ICurrentUserLoader>().LoadAsync(account.UserId, account.CompanyId);
        Assert.Equal(CurrentUserLoadResult.Loaded, result);
        return scope;
    }

    [Fact]
    public async Task The_application_registers_one_unit_of_work_and_one_audit_writer_instance_per_request()
    {
        var account = await TxHelpers.CreateAccountAsync(_fx);
        using var scope = await ScopeAsync(account);

        Assert.IsType<EfUnitOfWork>(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        Assert.Same(scope.ServiceProvider.GetRequiredService<IAuditWriter>(), scope.ServiceProvider.GetRequiredService<IAuditStagingState>());
        Assert.IsType<AuditWriter>(scope.ServiceProvider.GetRequiredService<IAuditWriter>());
    }

    [Fact]
    public async Task Through_the_real_pipeline_an_audited_command_commits_change_and_audit_once()
    {
        var account = await TxHelpers.CreateAccountAsync(_fx);
        using var scope = await ScopeAsync(account);

        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new TxCommand(account.CompanyId, "Via real pipeline", TxMode.Succeed));

        Assert.Equal(1, _counter.Started);
        Assert.Equal(1, _counter.Committed);
        Assert.Equal("Via real pipeline", (await TxHelpers.CompanyAsync(_fx, account.CompanyId)).Name);
        Assert.Single(await TxHelpers.AuditRowsAsync(_fx, account.CompanyId));
    }

    [Fact]
    public async Task Through_the_real_pipeline_a_failure_after_the_first_save_leaves_no_trace()
    {
        var account = await TxHelpers.CreateAccountAsync(_fx);
        var before = await TxHelpers.CompanyAsync(_fx, account.CompanyId);
        using var scope = await ScopeAsync(account);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IMediator>().Send(new TxCommand(account.CompanyId, "Must not survive", TxMode.ThrowAfterAuditStaged)));

        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, account.CompanyId));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, account.CompanyId));
        Assert.Equal(0, _counter.Committed);
    }

    [Fact]
    public async Task The_real_pipeline_validates_before_it_opens_a_transaction()
    {
        var account = await TxHelpers.CreateAccountAsync(_fx);
        using var scope = await ScopeAsync(account);

        await Assert.ThrowsAsync<ValidationException>(() => scope.ServiceProvider.GetRequiredService<IMediator>().Send(new InvalidTxCommand("")));

        Assert.Equal(0, _counter.Started);
    }

    [Fact]
    public async Task Login_is_not_wrapped_and_its_failed_attempt_audit_stays_independent()
    {
        var tenant = await TestData.CreateTenantAsync(_fx);

        var response = await _factory.ClientFor(tenant.Domain).PostLoginAsync(tenant.Email, "wrong-password-1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _counter.Started);                                       // no transaction was opened by the pipeline
        await using var db = _fx.CreateDbContext();
        var row = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.CompanyId == tenant.CompanyId);
        Assert.Equal("auth.login_failed", row.Action);                           // and the audit entry was still written
    }

    [Fact]
    public async Task Logout_and_me_are_not_wrapped_either()
    {
        var tenant = await TestData.CreateTenantAsync(_fx);
        var client = _factory.ClientFor(tenant.Domain);
        var login = await client.LoginAsync(tenant.Email, tenant.Password);

        Assert.Equal(HttpStatusCode.OK, (await client.GetMeAsync(login.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.LogoutAsync(login.Token)).StatusCode);

        Assert.Equal(0, _counter.Started);
    }
}
