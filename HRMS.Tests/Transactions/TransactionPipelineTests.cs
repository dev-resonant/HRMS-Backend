using FluentValidation;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Audit;
using HRMS.Infrastructure.Persistence;
using HRMS.Tests.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HRMS.Tests.Transactions;

/// <summary>
/// The transaction pattern against the guarded disposable PostgreSQL: real MediatR pipeline, real unit of work, real audit
/// writer, and a real <c>DbTransactionInterceptor</c> that counts the begin / commit / rollback events the database sees.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TransactionPipelineTests
{
    private readonly PostgresFixture _fx;

    public TransactionPipelineTests(PostgresFixture fx) => _fx = fx;

    private async Task<TxHost> HostAsync()
        => new(_fx, await TxHelpers.CreateAccountAsync(_fx));

    // ---------- commit together ----------

    [Fact]
    public async Task Business_change_and_audit_entry_commit_together_exactly_once()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);

        var returned = await host.SendAsync(new TxCommand(host.Account.CompanyId, "Renamed", TxMode.Succeed));

        Assert.Equal(before.Name, returned);
        Assert.Equal(1, host.Counter.Started);
        Assert.Equal(1, host.Counter.Committed);
        Assert.Equal(0, host.Counter.RolledBack);
        Assert.Equal("Renamed", (await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId)).Name);

        var row = Assert.Single(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
        Assert.Equal("company.updated", row.Action);
        Assert.Equal(host.Account.UserId, row.UserId);
        Assert.Contains("Renamed", row.NewValue);
    }

    [Fact]
    public async Task A_repository_style_save_inside_the_operation_does_not_commit_it_prematurely()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);

        await host.SendAsync(new TxCommand(host.Account.CompanyId, "Renamed", TxMode.Succeed));

        // Observed from a different connection right after the handler's own SaveChanges, before the operation finished.
        Assert.True(host.Probe.HadTransactionInHandler);
        Assert.Equal(before.Name, host.Probe.NameSeenByOtherConnectionAfterFirstSave);
        Assert.Equal(0, host.Probe.CommitsAfterFirstSave);
        Assert.Equal(1, host.Counter.Committed);                                // the single commit came at the very end
    }

    [Fact]
    public async Task A_staged_entry_the_handler_never_saved_is_flushed_inside_the_transaction_and_commits_with_it()
    {
        using var host = await HostAsync();

        await host.SendAsync(new TxCommand(host.Account.CompanyId, "Renamed", TxMode.ForgetFinalSave));

        Assert.Equal(1, host.Counter.Committed);
        Assert.Single(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
    }

    // ---------- rollback ----------

    [Fact]
    public async Task A_failure_after_a_successful_save_rolls_everything_back()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.SendAsync(new TxCommand(host.Account.CompanyId, "Must not survive", TxMode.ThrowAfterFirstSave)));

        Assert.Contains("simulated failure", failure.Message);                  // the original exception, not a wrapper
        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
        Assert.Equal(1, host.Counter.Started);
        Assert.Equal(0, host.Counter.Committed);
        Assert.Equal(1, host.Counter.RolledBack);
        Assert.Equal(before.Name, host.Probe.NameSeenByOtherConnectionAfterFirstSave);   // it was never visible either
    }

    [Fact]
    public async Task A_failure_after_the_audit_entry_was_staged_leaves_neither_change_nor_audit_row()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.SendAsync(new TxCommand(host.Account.CompanyId, "Must not survive", TxMode.ThrowAfterAuditStaged)));

        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
        Assert.Equal(0, host.Counter.Committed);
    }

    [Fact]
    public async Task An_immediate_bulk_update_is_rolled_back_too()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.SendAsync(new TxCommand(host.Account.CompanyId, "Must not survive", TxMode.ExecuteUpdateThenThrow)));

        var after = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);
        Assert.Equal(before.Logo, after.Logo);                                   // ExecuteUpdate joined the transaction
        Assert.Equal(before.Name, after.Name);
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
    }

    [Fact]
    public async Task Cancellation_rolls_back_safely()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);
        using var cts = new CancellationTokenSource();
        host.Probe.Cancel = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.SendAsync(new TxCommand(host.Account.CompanyId, "Must not survive", TxMode.CancelAfterFirstSave), cts.Token));

        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
        Assert.Equal(0, host.Counter.Committed);
        Assert.Equal(1, host.Counter.RolledBack);
    }

    [Fact]
    public async Task After_a_rollback_the_same_context_is_clean_and_usable()
    {
        using var host = await HostAsync();
        using var scope = host.Provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<HrmsDbContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mediator.Send(new TxCommand(host.Account.CompanyId, "Must not survive", TxMode.ThrowAfterAuditStaged)));

        Assert.Empty(db.ChangeTracker.Entries());                                // no stale "saved" entities or pending audit rows

        await mediator.Send(new TxCommand(host.Account.CompanyId, "Second attempt", TxMode.Succeed));   // same scope, same context

        Assert.Equal("Second attempt", (await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId)).Name);
        Assert.Single(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));      // only the successful attempt's entry
    }

    // ---------- audited requests ----------

    [Fact]
    public async Task An_audited_request_that_stages_no_entry_is_rolled_back()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.SendAsync(new TxCommand(host.Account.CompanyId, "Unaudited change", TxMode.NoAuditStaged)));

        Assert.Contains("audited request", failure.Message);
        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));    // the business change did not commit
        Assert.Equal(0, host.Counter.Committed);
    }

    [Fact]
    public async Task A_transactional_request_that_is_not_audited_may_commit_without_an_entry()
    {
        using var host = await HostAsync();

        await host.SendAsync(new TxPlainCommand(host.Account.CompanyId, "Plain change"));

        Assert.Equal("Plain change", (await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId)).Name);
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
        Assert.Equal(1, host.Counter.Committed);
    }

    // ---------- not wrapped ----------

    [Fact]
    public async Task A_read_only_request_is_not_wrapped_in_a_transaction()
    {
        using var host = await HostAsync();

        var name = await host.SendAsync(new ReadCompanyNameQuery(host.Account.CompanyId));

        Assert.StartsWith("Original", name);
        Assert.False(host.Probe.HadTransactionInHandler);
        Assert.Equal(0, host.Counter.Started);
        Assert.Equal(0, host.Counter.Committed);
        Assert.Equal(0, host.Counter.RolledBack);
    }

    [Fact]
    public async Task An_invalid_request_is_rejected_by_validation_before_any_transaction_is_opened()
    {
        using var host = await HostAsync();

        await Assert.ThrowsAsync<ValidationException>(() => host.SendAsync(new InvalidTxCommand("")));

        Assert.Equal(0, host.Counter.Started);
    }

    // ---------- joining instead of nesting ----------

    [Fact]
    public async Task A_transactional_request_sent_from_inside_another_joins_it_and_commits_once()
    {
        using var host = await HostAsync();

        await host.SendAsync(new OuterCommand(host.Account.CompanyId, "Outer", "Inner", InnerFails: false));

        Assert.Equal(1, host.Counter.Started);
        Assert.Equal(1, host.Counter.Committed);
        Assert.Equal("Inner", (await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId)).Name);
        Assert.Equal(2, (await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId)).Count);
    }

    [Fact]
    public async Task A_failure_in_the_joined_request_rolls_back_the_whole_outer_operation()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.SendAsync(new OuterCommand(host.Account.CompanyId, "Outer", "Inner", InnerFails: true)));

        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
        Assert.Equal(1, host.Counter.Started);
        Assert.Equal(0, host.Counter.Committed);
    }

    [Fact]
    public async Task The_unit_of_work_joins_an_existing_transaction_and_never_commits_it()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HrmsDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<HrmsDbContext>>();

        await using (var outer = await db.Database.BeginTransactionAsync())
        {
            await unitOfWork.ExecuteAsync(async ct =>
            {
                (await db.Companies.SingleAsync(c => c.Id == host.Account.CompanyId, ct)).Name = "Joined change";
                await db.SaveChangesAsync(ct);
                return 0;
            });

            Assert.NotNull(db.Database.CurrentTransaction);                                         // still the caller's, still open
            Assert.Equal(0, host.Counter.Committed);
            Assert.Equal(before.Name, await TxHelpers.NameViaOtherConnectionAsync(options, host.Account.CompanyId));

            await outer.RollbackAsync();
        }

        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));
    }

    // ---------- failed-login audit stays independent ----------

    [Fact]
    public async Task The_failed_login_audit_is_independent_of_an_open_unit_of_work()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HrmsDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<HrmsDbContext>>();
        string? seenByOtherConnection = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteAsync<int>(async ct =>
        {
            (await db.Companies.SingleAsync(c => c.Id == host.Account.CompanyId, ct)).Name = "Pending unit-of-work change";
            await db.SaveChangesAsync(ct);

            await audit.RecordFailedLoginAsync(host.Account.UserId, host.Account.CompanyId, ct);        // own context, own commit

            // The failed-login write committed, but it did not drag the unit of work's pending change with it.
            seenByOtherConnection = await TxHelpers.NameViaOtherConnectionAsync(options, host.Account.CompanyId);
            throw new InvalidOperationException("simulated failure of the surrounding operation");
        }));

        Assert.Equal(before.Name, seenByOtherConnection);
        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));                // business change rolled back
        var row = Assert.Single(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));            // failed-login row survived
        Assert.Equal("auth.login_failed", row.Action);
    }
}
