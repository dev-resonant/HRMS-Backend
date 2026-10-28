using System.Net;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Common;
using HRMS.Infrastructure.Outbox;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HRMS.Tests.Employees;

/// <summary>
/// The transactional outbox for invitations: the intent is written with the account, delivery happens afterwards outside any
/// transaction, failures are retried with backoff and end in a terminal state. Real API and PostgreSQL (guarded Testcontainers); the
/// delivery channel is a recording/failing test double - no provider exists in the product.
/// </summary>
[Collection(PostgresCollection.Name)]
public class InvitationOutboxTests : EmployeeApiTestBase
{
    private static readonly TokenHasher Hasher = new();

    public InvitationOutboxTests(PostgresFixture fx) : base(fx) { }

    private async Task<(AuthzCompany Company, AuthzUser Hr)> SetupAsync()
    {
        var company = await NewCompanyAsync();
        return (company, await HrAsync(company));
    }

    /// <summary>Creates an employee with the immediate delivery attempt switched off, so the message is left for the worker.</summary>
    private async Task<(Guid EmployeeId, Guid UserId, string Email)> CreateQueuedAsync(AuthzCompany company, AuthzUser hr)
    {
        OutboxSettings.InlineDelivery = false;

        var email = $"queued-{Guid.NewGuid():N}@example.test";
        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, email: email));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var employeeId = (await JsonOf(response)).GetProperty("id").GetGuid();

        return (employeeId, (await LoadAsync(employeeId)).User.Id, email);
    }

    private async Task<OutboxMessage> MessageOfAsync(Guid userId)
    {
        await using var db = Fx.CreateDbContext();
        return await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.UserId == userId);
    }

    private async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var db = Fx.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(sql, parameters);
    }

    /// <summary>Every row of the table rendered as text (to prove a value is stored nowhere); literal SQL per table, never composed.</summary>
    private async Task<string> TextOfAsync(string table)
    {
        await using var db = Fx.CreateDbContext();

        var rows = table switch
        {
            "OutboxMessages" => db.Database.SqlQueryRaw<string>("select coalesce(string_agg(t::text, ' '), '') as \"Value\" from \"OutboxMessages\" t"),
            "AccountSetupTokens" => db.Database.SqlQueryRaw<string>("select coalesce(string_agg(t::text, ' '), '') as \"Value\" from \"AccountSetupTokens\" t"),
            "AuditLogs" => db.Database.SqlQueryRaw<string>("select coalesce(string_agg(t::text, ' '), '') as \"Value\" from \"AuditLogs\" t"),
            "SecurityEvents" => db.Database.SqlQueryRaw<string>("select coalesce(string_agg(t::text, ' '), '') as \"Value\" from \"SecurityEvents\" t"),
            "Sessions" => db.Database.SqlQueryRaw<string>("select coalesce(string_agg(t::text, ' '), '') as \"Value\" from \"Sessions\" t"),
            "Users" => db.Database.SqlQueryRaw<string>("select coalesce(string_agg(t::text, ' '), '') as \"Value\" from \"Users\" t"),
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Add a literal query for this table.")
        };

        return await rows.SingleAsync();
    }

    // ================= written with the account =================

    [Fact]
    public async Task Creating_an_employee_records_the_delivery_intent_and_it_is_delivered_after_the_commit()
    {
        var (company, hr) = await SetupAsync();

        var created = await CreateViaApiAsync(company, hr);

        var message = await MessageOfAsync(created.UserId);
        var token = Assert.Single(await TokenRowsAsync(created.UserId));
        Assert.Equal(company.CompanyId, message.CompanyId);
        Assert.Equal(OutboxMessage.AccountInvitationKind, message.Kind);
        Assert.Equal(token.Id, message.SetupTokenId);
        Assert.Equal(OutboxStatus.Sent, message.Status);
        Assert.Equal(1, message.Attempts);
        Assert.NotNull(message.ProcessedAt);
        Assert.Null(message.LockedUntil);
        Assert.Null(message.LastError);
    }

    [Fact]
    public async Task A_rolled_back_creation_leaves_no_message_to_deliver()
    {
        var (company, hr) = await SetupAsync();
        Switch.AuditStageFails = true;

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await OutboxAsync(company.CompanyId));
        Assert.Empty(Sender.Sent);
        Assert.Equal(0, await ProcessOutboxAsync());                                    // nothing the worker could ever pick up
    }

    [Fact]
    public async Task Nothing_in_the_outbox_or_anywhere_else_can_be_used_to_log_in()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        var raw = created.Invitation.Token;

        foreach (var table in new[] { "OutboxMessages", "AccountSetupTokens", "AuditLogs", "SecurityEvents", "Sessions", "Users" })
        {
            var text = await TextOfAsync(table);
            Assert.DoesNotContain(raw, text);                                          // not the token...
        }

        Assert.Contains(Hasher.Hash(raw), await TextOfAsync("AccountSetupTokens"));    // (the probe itself works: the hash IS stored)

        var outbox = await TextOfAsync("OutboxMessages");
        Assert.DoesNotContain(created.Email, outbox);                                  // ...and not even the recipient address
        Assert.DoesNotContain(Hasher.Hash(raw), outbox);
        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains(raw) || e.Message.Contains(created.Email));
    }

    [Fact]
    public async Task One_token_can_have_only_one_message_and_unknown_states_are_refused_by_the_database()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        var existing = await MessageOfAsync(created.UserId);

        await using (var db = Fx.CreateDbContext())
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(), CompanyId = existing.CompanyId, UserId = existing.UserId, SetupTokenId = existing.SetupTokenId,
                NextAttemptAt = DateTime.UtcNow
            });

            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("IX_OutboxMessages_SetupTokenId", duplicate.InnerException!.Message);
        }

        var bogus = await Assert.ThrowsAnyAsync<Exception>(() =>
            ExecuteAsync("update \"OutboxMessages\" set \"Status\" = 'Bogus' where \"Id\" = {0}", existing.Id));
        Assert.Contains("CK_OutboxMessages_Status", bogus.ToString());
    }

    // ================= retries =================

    [Fact]
    public async Task A_failed_delivery_is_retried_after_the_backoff_with_a_fresh_working_token()
    {
        var (company, hr) = await SetupAsync();
        Sender.ThrowOnSend = true;

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, email: "retry@example.test"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);                      // the employee exists regardless
        var userId = (await LoadAsync((await JsonOf(response)).GetProperty("id").GetGuid())).User.Id;
        var failed = await MessageOfAsync(userId);
        Assert.Equal(OutboxStatus.Pending, failed.Status);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal("InvalidOperationException", failed.LastError);                    // the exception TYPE only
        Assert.Equal(Clock.GetUtcNow().UtcDateTime.AddSeconds(30), failed.NextAttemptAt, TimeSpan.FromMilliseconds(5));
        Assert.Empty(Sender.Sent);
        var expiry = Assert.Single(await TokenRowsAsync(userId)).ExpiresAt;

        Assert.Equal(0, await ProcessOutboxAsync());                                    // not due yet
        Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, await ProcessOutboxAsync());

        Clock.Advance(TimeSpan.FromSeconds(2));
        Sender.ThrowOnSend = false;
        Assert.Equal(1, await ProcessOutboxAsync());

        var sent = await MessageOfAsync(userId);
        Assert.Equal(OutboxStatus.Sent, sent.Status);
        Assert.Equal(2, sent.Attempts);
        Assert.Null(sent.LastError);
        var invitation = Assert.Single(Sender.Sent);
        Assert.Equal(expiry, invitation.ExpiresAtUtc, TimeSpan.FromMilliseconds(1));    // the promised lifetime does not move
        Assert.Equal(Hasher.Hash(invitation.Token), Assert.Single(await TokenRowsAsync(userId)).TokenHash);
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, invitation.Token, StrongPassword)).StatusCode);
        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains("retry@example.test") || e.Message.Contains("simulated"));
    }

    [Fact]
    public void The_delay_doubles_from_the_base_up_to_the_cap_without_overflowing()
    {
        var options = new OutboxOptions { BaseDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromHours(1) };

        Assert.Equal(TimeSpan.FromSeconds(30), options.DelayAfter(1));
        Assert.Equal(TimeSpan.FromSeconds(60), options.DelayAfter(2));
        Assert.Equal(TimeSpan.FromSeconds(120), options.DelayAfter(3));
        Assert.Equal(TimeSpan.FromSeconds(960), options.DelayAfter(6));
        Assert.Equal(TimeSpan.FromHours(1), options.DelayAfter(8));                      // 30s * 2^7 = 64 min -> capped
        Assert.Equal(TimeSpan.FromHours(1), options.DelayAfter(500));
        Assert.Equal(TimeSpan.FromSeconds(30), options.DelayAfter(0));
    }

    [Fact]
    public async Task After_the_last_attempt_the_message_is_terminal_visible_and_never_retried()
    {
        var (company, hr) = await SetupAsync();
        OutboxSettings.MaxAttempts = 3;
        Sender.ThrowOnSend = true;

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, email: "terminal@example.test"));
        var employeeId = (await JsonOf(response)).GetProperty("id").GetGuid();
        var userId = (await LoadAsync(employeeId)).User.Id;

        Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await ProcessOutboxAsync());                                    // attempt 2
        var second = await MessageOfAsync(userId);
        Assert.Equal(OutboxStatus.Pending, second.Status);
        Assert.Equal(Clock.GetUtcNow().UtcDateTime.AddSeconds(60), second.NextAttemptAt, TimeSpan.FromMilliseconds(5));   // doubled

        Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await ProcessOutboxAsync());                                    // attempt 3 = the last one

        var terminal = await MessageOfAsync(userId);
        Assert.Equal(OutboxStatus.Failed, terminal.Status);
        Assert.Equal(3, terminal.Attempts);
        Assert.NotNull(terminal.ProcessedAt);
        Assert.Equal("InvalidOperationException", terminal.LastError);
        Assert.Contains(Logs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && e.Message.Contains(terminal.Id.ToString()) && e.Message.Contains("terminal") && e.Message.Contains("failed with InvalidOperationException"));
        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains("terminal@example.test") || e.Message.Contains("simulated"));

        Clock.Advance(TimeSpan.FromHours(10));
        Sender.ThrowOnSend = false;
        Assert.Equal(0, await ProcessOutboxAsync());                                    // never again, even though the channel works now
        Assert.Empty(Sender.Sent);

        // The documented way out: an administrator re-issues the invitation, which creates a new message.
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(company, HttpMethod.Post, $"/api/employees/{employeeId}/invitation", hr.Token)).StatusCode);
        Assert.Single(Sender.Sent);
        Assert.Equal(2, (await OutboxAsync(company.CompanyId)).Count(m => m.UserId == userId));
    }

    [Fact]
    public async Task Without_a_delivery_channel_messages_wait_and_burn_no_attempts_until_one_exists()
    {
        var (company, hr) = await SetupAsync();
        Sender.IsConfigured = false;
        var queued = await CreateQueuedAsync(company, hr);

        Assert.Equal(0, await ProcessOutboxAsync());
        Assert.Equal(0, await ProcessOutboxAsync());
        var waiting = await MessageOfAsync(queued.UserId);
        Assert.Equal(OutboxStatus.Pending, waiting.Status);
        Assert.Equal(0, waiting.Attempts);

        Sender.IsConfigured = true;
        Assert.Equal(1, await ProcessOutboxAsync());

        Assert.Equal(OutboxStatus.Sent, (await MessageOfAsync(queued.UserId)).Status);
        Assert.Equal(queued.Email, Assert.Single(Sender.Sent).RecipientEmail);
    }

    [Fact]
    public async Task A_message_that_waited_past_the_lifetime_is_cancelled_instead_of_sent()
    {
        var (company, hr) = await SetupAsync();
        Sender.IsConfigured = false;
        var queued = await CreateQueuedAsync(company, hr);

        Clock.Advance(TimeSpan.FromHours(73));
        Sender.IsConfigured = true;
        Assert.Equal(1, await ProcessOutboxAsync());

        var message = await MessageOfAsync(queued.UserId);
        Assert.Equal(OutboxStatus.Cancelled, message.Status);
        Assert.Equal("token_expired", message.LastError);
        Assert.Empty(Sender.Sent);                                                       // an expired link is never mailed
    }

    // ================= messages that became pointless =================

    [Fact]
    public async Task A_replaced_invitation_is_cancelled_and_only_the_newest_is_delivered()
    {
        var (company, hr) = await SetupAsync();
        Sender.IsConfigured = false;
        var queued = await CreateQueuedAsync(company, hr);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(company, HttpMethod.Post, $"/api/employees/{queued.EmployeeId}/invitation", hr.Token)).StatusCode);

        Sender.IsConfigured = true;
        Assert.Equal(2, await ProcessOutboxAsync());

        var messages = (await OutboxAsync(company.CompanyId)).Where(m => m.UserId == queued.UserId).ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("token_not_usable", Assert.Single(messages, m => m.Status == OutboxStatus.Cancelled).LastError);   // (the frozen test clock gives both the same CreatedAt, so no ordering is assumed)
        Assert.Single(messages, m => m.Status == OutboxStatus.Sent);
        var invitation = Assert.Single(Sender.Sent);
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, invitation.Token, StrongPassword)).StatusCode);
    }

    [Theory]
    [InlineData("deactivated", "account_inactive")]
    [InlineData("already set up", "account_already_set_up")]
    public async Task A_message_for_an_account_that_no_longer_needs_the_invitation_is_cancelled(string change, string expectedReason)
    {
        var (company, hr) = await SetupAsync();
        Sender.IsConfigured = false;
        var queued = await CreateQueuedAsync(company, hr);

        await using (var db = Fx.CreateDbContext())
        {
            if (change == "deactivated")
            {
                await db.Users.Where(u => u.Id == queued.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
            }
            else
            {
                await db.Users.Where(u => u.Id == queued.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsFirstLogin, false));
            }
        }

        Sender.IsConfigured = true;
        Assert.Equal(1, await ProcessOutboxAsync());

        var message = await MessageOfAsync(queued.UserId);
        Assert.Equal(OutboxStatus.Cancelled, message.Status);
        Assert.Equal(expectedReason, message.LastError);
        Assert.Empty(Sender.Sent);
    }

    [Fact]
    public async Task A_consumed_token_is_never_revived_by_a_late_delivery()
    {
        var (company, hr) = await SetupAsync();
        Sender.IsConfigured = false;
        var queued = await CreateQueuedAsync(company, hr);
        var token = Assert.Single(await TokenRowsAsync(queued.UserId));
        var hashBefore = token.TokenHash;

        await ExecuteAsync("update \"AccountSetupTokens\" set \"ConsumedAt\" = now() where \"Id\" = {0}", token.Id);

        Sender.IsConfigured = true;
        await ProcessOutboxAsync();

        var after = Assert.Single(await TokenRowsAsync(queued.UserId));
        Assert.Equal(hashBefore, after.TokenHash);                                       // untouched: the rotation needs an open token
        Assert.NotNull(after.ConsumedAt);
        Assert.Empty(Sender.Sent);
    }

    // ================= concurrency and crashes =================

    [Fact]
    public async Task Concurrent_workers_deliver_a_message_exactly_once()
    {
        var (company, hr) = await SetupAsync();
        var queued = await CreateQueuedAsync(company, hr);
        Sender.OnSend = _ => Task.Delay(300);                                            // keep the first claim busy while the others try

        var claimed = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(ProcessOutboxAsync)));

        Assert.Equal(1, claimed.Sum());
        Assert.Single(Sender.Sent);
        var message = await MessageOfAsync(queued.UserId);
        Assert.Equal(OutboxStatus.Sent, message.Status);
        Assert.Equal(1, message.Attempts);
    }

    [Fact]
    public async Task Concurrent_workers_split_a_backlog_without_sending_anything_twice()
    {
        var (company, hr) = await SetupAsync();
        var users = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            users.Add((await CreateQueuedAsync(company, hr)).UserId);
        }

        var claimed = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(ProcessOutboxAsync)));

        Assert.Equal(12, claimed.Sum());
        Assert.Equal(12, Sender.Sent.Count);
        Assert.Equal(users.OrderBy(u => u), Sender.Sent.Select(i => i.UserId).OrderBy(u => u));          // each exactly once
        Assert.All(await OutboxAsync(company.CompanyId), m => Assert.Equal((OutboxStatus.Sent, 1), (m.Status, m.Attempts)));
    }

    [Fact]
    public async Task A_dead_workers_claim_is_respected_until_its_lease_ends_and_then_taken_over()
    {
        var (company, hr) = await SetupAsync();
        var queued = await CreateQueuedAsync(company, hr);
        var lease = Clock.GetUtcNow().UtcDateTime.AddMinutes(2);
        await ExecuteAsync("update \"OutboxMessages\" set \"Status\" = 'Processing', \"Attempts\" = 1, \"LockedUntil\" = {0} where \"UserId\" = {1}",
            lease, queued.UserId);

        Assert.Equal(0, await ProcessOutboxAsync());                                     // another worker still owns it
        Assert.Empty(Sender.Sent);

        Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await ProcessOutboxAsync());                                     // lease over: taken over

        var message = await MessageOfAsync(queued.UserId);
        Assert.Equal(OutboxStatus.Sent, message.Status);
        Assert.Equal(2, message.Attempts);
        Assert.Single(Sender.Sent);
    }

    [Fact]
    public async Task A_claim_that_expired_with_the_attempts_used_up_becomes_terminal_instead_of_looping()
    {
        var (company, hr) = await SetupAsync();
        var queued = await CreateQueuedAsync(company, hr);
        await ExecuteAsync("update \"OutboxMessages\" set \"Status\" = 'Processing', \"Attempts\" = {0}, \"LockedUntil\" = {1} where \"UserId\" = {2}",
            OutboxSettings.MaxAttempts,
            Clock.GetUtcNow().UtcDateTime.AddMinutes(-1),
            queued.UserId);

        Assert.Equal(0, await ProcessOutboxAsync());

        var message = await MessageOfAsync(queued.UserId);
        Assert.Equal(OutboxStatus.Failed, message.Status);
        Assert.Equal("abandoned", message.LastError);
        Assert.Empty(Sender.Sent);
        Assert.Contains(Logs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error && e.Message.Contains("abandoned"));
    }

    [Fact]
    public async Task Delivery_happens_outside_any_transaction_and_holds_no_row_lock()
    {
        var (company, hr) = await SetupAsync();
        var queued = await CreateQueuedAsync(company, hr);
        string? stateDuringSend = null;
        Exception? lockProblem = null;

        Sender.OnSend = async invitation =>
        {
            await using var other = Fx.CreateDbContext();
            await using var transaction = await other.Database.BeginTransactionAsync();

            try
            {
                // NOWAIT fails immediately if any other session still holds a lock on the row.
                await other.Database.ExecuteSqlRawAsync("select 1 from \"OutboxMessages\" where \"UserId\" = {0} for update nowait", invitation.UserId);
                stateDuringSend = await other.Database.SqlQueryRaw<string>("select \"Status\" as \"Value\" from \"OutboxMessages\" where \"UserId\" = {0}", invitation.UserId).SingleAsync();
            }
            catch (Exception ex)
            {
                lockProblem = ex;
            }
        };

        await ProcessOutboxAsync();

        Assert.Null(lockProblem);
        Assert.Equal(OutboxStatus.Processing, stateDuringSend);                           // visible, claimed, and not locked
        Assert.Equal(OutboxStatus.Sent, (await MessageOfAsync(queued.UserId)).Status);
    }

    [Fact]
    public async Task A_worker_that_is_shutting_down_gives_the_message_back_without_costing_an_attempt()
    {
        var (company, hr) = await SetupAsync();
        var queued = await CreateQueuedAsync(company, hr);
        using var shutdown = new CancellationTokenSource();
        Sender.OnSend = _ =>
        {
            shutdown.Cancel();
            throw new OperationCanceledException(shutdown.Token);
        };

        using (var scope = Factory.Services.CreateScope())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => scope.ServiceProvider.GetRequiredService<InvitationOutboxProcessor>().ProcessDueAsync(shutdown.Token));
        }

        var released = await MessageOfAsync(queued.UserId);
        Assert.Equal(OutboxStatus.Pending, released.Status);
        Assert.Equal(0, released.Attempts);
        Assert.Null(released.LockedUntil);

        Sender.OnSend = null;
        Assert.Equal(1, await ProcessOutboxAsync());
        Assert.Equal(OutboxStatus.Sent, (await MessageOfAsync(queued.UserId)).Status);
    }
}

/// <summary>The polling worker itself (hosted service), with the immediate attempt turned off so only the worker can deliver.</summary>
[Collection(PostgresCollection.Name)]
public class OutboxWorkerTests : EmployeeApiTestBase
{
    public OutboxWorkerTests(PostgresFixture fx) : base(fx, settings: new Dictionary<string, string?>
    {
        ["Outbox:WorkerEnabled"] = "true",
        ["Outbox:InlineDelivery"] = "false",
        ["Outbox:PollInterval"] = "00:00:00.200"
    })
    {
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    [Fact]
    public async Task The_worker_delivers_a_pending_message_and_retries_a_failed_one_when_it_is_due()
    {
        var company = await NewCompanyAsync();
        var hr = await HrAsync(company);
        Sender.ThrowOnSend = true;

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The worker tries (and fails), recording the failure.
        Assert.True(await WaitUntilAsync(async () => (await OutboxAsync(company.CompanyId)).Any(m => m.Attempts >= 1 && m.Status == OutboxStatus.Pending)));
        Assert.Empty(Sender.Sent);

        // The channel recovers and the backoff passes: the next poll delivers.
        Sender.ThrowOnSend = false;
        Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(await WaitUntilAsync(() => Task.FromResult(Sender.Sent.Count == 1)));
        Assert.True(await WaitUntilAsync(async () => (await OutboxAsync(company.CompanyId)).Single().Status == OutboxStatus.Sent));
        Assert.Equal(2, (await OutboxAsync(company.CompanyId)).Single().Attempts);
    }
}
