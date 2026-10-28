using HRMS.Application.Common.Transactions;
using HRMS.Application.Features.Auth.Commands.ChangePassword;
using HRMS.Application.Features.Auth.Commands.SetPassword;
using HRMS.Application.Features.Employees.Commands.IssueInvitation;
using HRMS.Application.Interfaces;
using HRMS.Infrastructure.Common;
using HRMS.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HRMS.Tests.Employees;

public class OnboardingArchitectureTests
{
    [Theory]
    [InlineData(typeof(SetPasswordCommand), "auth", "auth.password_set")]
    [InlineData(typeof(ChangePasswordCommand), "auth", "auth.password_changed")]
    [InlineData(typeof(IssueInvitationCommand), "employee", "employee.invitation_issued")]
    public void Every_security_sensitive_command_is_transactional_and_audited_with_its_exact_entry(Type command, string module, string action)
    {
        Assert.True(typeof(IAuditedRequest).IsAssignableFrom(command));
        Assert.Equal(new[] { new AuditExpectation(module, action) }, AuditExpectationAttribute.For(command));
    }

    [Theory]
    [InlineData(typeof(SetPasswordCommand))]
    [InlineData(typeof(ChangePasswordCommand))]
    [InlineData(typeof(IssueInvitationCommand))]
    public void The_commands_carry_no_company_user_or_audit_attribution_from_the_client(Type command)
    {
        var forbidden = new[] { "company", "actor", "userid", "role" };

        Assert.All(command.GetProperties().Where(p => p.DeclaringType == command),
            p => Assert.DoesNotContain(forbidden, f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void An_invitation_prints_without_its_token()
    {
        var invitation = new AccountInvitation(Guid.NewGuid(), Guid.NewGuid(), "Asha", "asha@example.test", "x.test", "SECRET-TOKEN-VALUE", DateTime.UtcNow);

        var printed = invitation.ToString() + $"{invitation}" + string.Format("{0}", invitation);

        Assert.DoesNotContain("SECRET-TOKEN-VALUE", printed);
        Assert.DoesNotContain("asha@example.test", printed);
    }

    [Fact]
    public void Generated_tokens_are_long_random_and_url_safe()
    {
        var tokens = Enumerable.Range(0, 500).Select(_ => HRMS.Application.Features.Auth.AccountInvitationIssuer.NewToken()).ToList();

        Assert.Equal(500, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]{43}$", t));
    }

    [Fact]
    public void Without_a_provider_the_default_channel_reports_itself_as_not_configured()
    {
        var sender = new NotConfiguredInvitationSender(Microsoft.Extensions.Logging.Abstractions.NullLogger<NotConfiguredInvitationSender>.Instance);

        Assert.False(sender.IsConfigured);
    }
}

[Collection(PostgresCollection.Name)]
public class OnboardingWiringTests : IDisposable
{
    private readonly HrmsWebApplicationFactory _factory;

    public OnboardingWiringTests(PostgresFixture fx)
    {
        _factory = new HrmsWebApplicationFactory(fx);
        _factory.AssertUsesFixtureDatabase();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void The_product_ships_no_delivery_provider_and_a_72_hour_default_lifetime()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.IsType<NotConfiguredInvitationSender>(scope.ServiceProvider.GetRequiredService<IAccountInvitationSender>());
        Assert.False(scope.ServiceProvider.GetRequiredService<IAccountInvitationSender>().IsConfigured);
        Assert.Equal(TimeSpan.FromHours(72), scope.ServiceProvider.GetRequiredService<AccountSetupOptions>().InvitationLifetime);
        Assert.Same(scope.ServiceProvider.GetRequiredService<IPostCommitActions>(), scope.ServiceProvider.GetRequiredService<PostCommitActions>());
    }
}

public class PostCommitActionsTests
{
    private sealed class ListLogger : ILogger<PostCommitActions>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + (exception is null ? "" : " [exception object attached]"));
    }

    [Fact]
    public async Task Actions_run_once_in_order_and_a_failure_neither_stops_the_rest_nor_leaks_its_message()
    {
        var logger = new ListLogger();
        var queue = new PostCommitActions(logger);
        var ran = new List<string>();
        queue.Enqueue("first", _ => { ran.Add("first"); return Task.CompletedTask; });
        queue.Enqueue("broken", _ => throw new InvalidOperationException("recipient jane@secret.test and token abc"));
        queue.Enqueue("third", _ => { ran.Add("third"); return Task.CompletedTask; });

        await queue.RunAsync();
        await queue.RunAsync();                                                   // the queue is empty now: nothing runs twice

        Assert.Equal(new[] { "first", "third" }, ran);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("broken", message);
        Assert.Contains("InvalidOperationException", message);
        Assert.DoesNotContain("jane@secret.test", message);
        Assert.DoesNotContain("abc", message);
        Assert.DoesNotContain("exception object attached", message);              // the exception (and its message) is not handed to the logger
    }

    [Fact]
    public async Task Clearing_the_queue_discards_everything_for_a_rolled_back_operation()
    {
        var queue = new PostCommitActions(new ListLogger());
        var ran = false;
        queue.Enqueue("never", _ => { ran = true; return Task.CompletedTask; });

        queue.Clear();
        await queue.RunAsync();

        Assert.False(ran);
        Assert.Equal(0, queue.Pending);
    }
}
