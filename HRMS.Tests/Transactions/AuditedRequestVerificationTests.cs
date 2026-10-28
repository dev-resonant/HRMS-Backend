using HRMS.Application.Audit;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Interfaces;
using HRMS.Infrastructure.Persistence;
using HRMS.Tests.Infrastructure;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace HRMS.Tests.Transactions;

/// <summary>
/// Declared audit action (module + action) verification and commit-cancellation handling, against the guarded disposable
/// PostgreSQL. What these tests prove is exactly: an entry with the declared module and action, staged DURING the command,
/// is required for the commit. They do not (and the production code does not) prove that target or values are correct.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AuditedRequestVerificationTests
{
    private readonly PostgresFixture _fx;

    public AuditedRequestVerificationTests(PostgresFixture fx) => _fx = fx;

    private async Task<TxHost> HostAsync() => new(_fx, await TxHelpers.CreateAccountAsync(_fx));

    private static async Task<InvalidOperationException> FailureOf(Func<Task> action)
        => await Assert.ThrowsAsync<InvalidOperationException>(action);

    // ---------- declared module/action ----------

    [Fact]
    public async Task The_declared_module_and_action_pass_and_commit()
    {
        using var host = await HostAsync();

        await host.SendAsync(new TxCommand(host.Account.CompanyId, "Renamed", TxMode.Succeed));

        var row = Assert.Single(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
        Assert.Equal("company", row.Module);
        Assert.Equal("company.updated", row.Action);
        Assert.Equal(1, host.Counter.Committed);
    }

    [Fact]
    public async Task A_missing_matching_entry_rolls_back_and_the_message_names_what_was_expected_and_staged()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);

        var failure = await FailureOf(() => host.SendAsync(new TxCommand(host.Account.CompanyId, "Nope", TxMode.NoAuditStaged)));

        Assert.Contains("company/company.updated", failure.Message);
        Assert.Contains("Staged during the command: none", failure.Message);
        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
        Assert.Equal(0, host.Counter.Committed);
    }

    [Fact]
    public async Task A_wrong_action_rolls_back_everything()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);

        var failure = await FailureOf(() => host.SendAsync(new TxCommand(host.Account.CompanyId, "Nope", TxMode.StageWrongAction)));

        Assert.Contains("company/company.deleted", failure.Message);                      // what was actually staged
        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));        // the wrong entry did not commit either
        Assert.Equal(0, host.Counter.Committed);
    }

    [Fact]
    public async Task A_wrong_module_rolls_back_everything()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);

        await FailureOf(() => host.SendAsync(new TxCommand(host.Account.CompanyId, "Nope", TxMode.StageWrongModule)));

        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
    }

    [Fact]
    public async Task Additional_entries_next_to_the_declared_one_are_allowed_and_commit()
    {
        using var host = await HostAsync();

        await host.SendAsync(new TxCommand(host.Account.CompanyId, "Renamed", TxMode.StageExpectedAndExtra));

        var rows = await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId);
        Assert.Equal(new[] { "company.updated", "company.viewed" }, rows.Select(r => r.Action).OrderBy(a => a));
    }

    // ---------- an unrelated earlier entry cannot satisfy the check ----------

    [Fact]
    public async Task An_entry_staged_earlier_in_the_same_request_cannot_satisfy_the_command()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HrmsDbContext>();

        // Some other code in the same request already staged an entry with exactly the declared module and action.
        scope.ServiceProvider.GetRequiredService<IAuditWriter>().Stage(new AuditEntry("company", "company.updated", host.Account.CompanyId));

        await FailureOf(() => scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new TxCommand(host.Account.CompanyId, "Nope", TxMode.NoAuditStaged)));

        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));        // neither the command's change nor the earlier entry
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task An_entry_from_an_earlier_successful_command_in_the_same_scope_cannot_satisfy_the_next_one()
    {
        using var host = await HostAsync();
        using var scope = host.Provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new TxCommand(host.Account.CompanyId, "First", TxMode.Succeed));

        await FailureOf(() => mediator.Send(new TxCommand(host.Account.CompanyId, "Second", TxMode.NoAuditStaged)));

        Assert.Equal("First", (await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId)).Name);       // only the first committed
        Assert.Single(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
    }

    [Fact]
    public async Task An_enclosing_commands_entry_cannot_satisfy_a_joined_command_that_staged_nothing()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);

        var failure = await FailureOf(() => host.SendAsync(new WindowOuterCommand(host.Account.CompanyId)));

        Assert.Contains(nameof(WindowInnerCommand), failure.Message);
        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));            // the outer's change and entry roll back too
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
        Assert.Equal(1, host.Counter.Started);
        Assert.Equal(0, host.Counter.Committed);
    }

    // ---------- several declarations ----------

    [Fact]
    public async Task Every_declaration_needs_its_own_matching_entry()
    {
        using var host = await HostAsync();

        await host.SendAsync(new MultiAuditCommand(host.Account.CompanyId, new[] { "company.updated", "company.renamed" }));

        Assert.Equal(2, (await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId)).Count);
    }

    [Fact]
    public async Task One_of_two_declared_entries_is_not_enough()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);

        var failure = await FailureOf(() => host.SendAsync(new MultiAuditCommand(host.Account.CompanyId, new[] { "company.updated" })));

        Assert.Contains("company/company.renamed", failure.Message);
        Assert.DoesNotContain("expected audit entry: company/company.updated", failure.Message);
        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
    }

    [Fact]
    public async Task One_entry_cannot_be_counted_twice_to_cover_two_declarations()
    {
        using var host = await HostAsync();

        // Two "updated" entries cannot stand in for "updated" + "renamed".
        await FailureOf(() => host.SendAsync(new MultiAuditCommand(host.Account.CompanyId, new[] { "company.updated", "company.updated" })));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));

        // One entry cannot satisfy a declaration that was made twice; two entries can.
        await FailureOf(() => host.SendAsync(new DuplicateExpectationCommand(host.Account.CompanyId, 1)));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));

        await host.SendAsync(new DuplicateExpectationCommand(host.Account.CompanyId, 2));
        Assert.Equal(2, (await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId)).Count);
    }

    // ---------- fail closed without a declaration ----------

    [Fact]
    public async Task An_audited_command_that_declares_nothing_is_refused_before_it_runs()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);

        var failure = await FailureOf(() => host.SendAsync(new UndeclaredAuditedCommand(host.Account.CompanyId)));

        Assert.Contains("declares no [AuditExpectation]", failure.Message);
        Assert.Equal(0, host.Counter.Started);                                                      // no transaction was even opened
        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));            // and the handler never ran
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
    }

    [Theory]
    [InlineData("", "company.updated")]
    [InlineData("Company", "company.updated")]
    [InlineData("company", "updated")]
    [InlineData("company", "Company.Updated")]
    public void A_declaration_with_invalid_names_cannot_be_created(string module, string action)
        => Assert.Throws<ArgumentException>(() => new AuditExpectationAttribute(module, action));

    // ---------- commit cancellation and failures around the commit ----------

    [Fact]
    public async Task Cancelling_the_request_just_before_the_commit_does_not_interrupt_the_commit()
    {
        using var host = await HostAsync();
        using var cts = new CancellationTokenSource();
        var fired = false;
        host.Hooks.OnCommitting = () =>
        {
            fired = true;
            cts.Cancel();                                        // the client disconnects at the last moment
        };

        await host.SendAsync(new TxCommand(host.Account.CompanyId, "Committed despite cancel", TxMode.Succeed), cts.Token);

        Assert.True(fired);
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal("Committed despite cancel", (await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId)).Name);
        Assert.Single(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
        Assert.Equal(1, host.Counter.Committed);
        Assert.Equal(0, host.Counter.RolledBack);
    }

    [Fact]
    public async Task Cancelling_before_the_final_flush_still_rolls_everything_back()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);
        using var cts = new CancellationTokenSource();
        host.Probe.Cancel = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.SendAsync(new TxCommand(host.Account.CompanyId, "Nope", TxMode.CancelThenReturnWithoutSaving), cts.Token));

        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
        Assert.Equal(0, host.Counter.Committed);
        Assert.Equal(1, host.Counter.RolledBack);
    }

    [Fact]
    public async Task A_rollback_that_itself_fails_does_not_replace_the_original_exception()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);
        host.Hooks.OnRollingBack = () => throw new InvalidOperationException("rollback hook failure");

        var failure = await FailureOf(() => host.SendAsync(new TxCommand(host.Account.CompanyId, "Nope", TxMode.ThrowAfterFirstSave)));

        Assert.Contains("simulated failure after the first successful save", failure.Message);       // the handler's exception
        Assert.DoesNotContain("rollback hook failure", failure.Message);
        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));              // nothing leaked: disposal discarded it
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));

        host.Hooks.OnRollingBack = null;
        await host.SendAsync(new TxCommand(host.Account.CompanyId, "Works afterwards", TxMode.Succeed));   // the connection pool is healthy
        Assert.Equal("Works afterwards", (await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId)).Name);
    }

    [Fact]
    public async Task A_failing_commit_is_rolled_back_and_surfaces_its_own_exception()
    {
        using var host = await HostAsync();
        var before = await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId);
        host.Hooks.OnCommitting = () => throw new InvalidOperationException("commit hook failure");

        var failure = await FailureOf(() => host.SendAsync(new TxCommand(host.Account.CompanyId, "Nope", TxMode.Succeed)));

        Assert.Contains("commit hook failure", failure.Message);
        Assert.Equal(before, await TxHelpers.CompanyAsync(_fx, host.Account.CompanyId));
        Assert.Empty(await TxHelpers.AuditRowsAsync(_fx, host.Account.CompanyId));
        Assert.Equal(0, host.Counter.Committed);

        host.Hooks.OnCommitting = null;
        await host.SendAsync(new TxCommand(host.Account.CompanyId, "Works afterwards", TxMode.Succeed));
        Assert.Equal(1, host.Counter.Committed);
    }
}
