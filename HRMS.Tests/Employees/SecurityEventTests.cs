using System.Net;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Audit;
using HRMS.Infrastructure.Persistence;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HRMS.Tests.Employees;

/// <summary>
/// Rejected password operations. An anonymous request with a bad setup token has no acting user, and audit_log.user_id is a required
/// foreign key, so such attempts go to the append-only security_events table (nullable user) instead of a fabricated audit row.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SecurityEventTests : EmployeeApiTestBase
{
    private const string OldPassword = "Old-Password-Value-1!";

    public SecurityEventTests(PostgresFixture fx) : base(fx) { }

    private async Task<(AuthzCompany Company, AuthzUser Hr)> SetupAsync()
    {
        var company = await NewCompanyAsync();
        return (company, await HrAsync(company));
    }

    private async Task<string> EventsTextAsync()
    {
        await using var db = Fx.CreateDbContext();
        return await db.Database.SqlQueryRaw<string>("select coalesce(string_agg(t::text, ' '), '') as \"Value\" from \"SecurityEvents\" t").SingleAsync();
    }

    // ================= set-password =================

    [Fact]
    public async Task An_unknown_token_is_recorded_without_inventing_a_user_and_without_touching_the_audit_log()
    {
        var (company, _) = await SetupAsync();
        var auditBefore = (await AuditAsync(company.CompanyId)).Count;
        var guess = AccountInvitationIssuerToken();

        var response = await SetPasswordAsync(company, guess, StrongPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var entry = Assert.Single(await SecurityEventsAsync(company.CompanyId));
        Assert.Equal(SecurityEventTypes.SetPasswordFailed, entry.EventType);
        Assert.Equal(SecurityEventReasons.InvalidToken, entry.Reason);
        Assert.Null(entry.UserId);                                                          // nobody is named that the server does not know
        Assert.Equal(company.CompanyId, entry.CompanyId);
        Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, entry.IpAddress);
        Assert.Equal(auditBefore, (await AuditAsync(company.CompanyId)).Count);             // the audit log needs a real actor: nothing was added

        var text = await EventsTextAsync() + await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(guess, text);
        Assert.DoesNotContain(StrongPassword, text);
        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains(guess) || e.Message.Contains(StrongPassword));
    }

    [Fact]
    public async Task Expired_replayed_replaced_and_foreign_tokens_are_each_recorded_in_the_tenant_that_received_them()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var expired = await CreateViaApiAsync(company, hr);
        var used = await CreateViaApiAsync(company, hr);
        var replaced = await CreateViaApiAsync(company, hr);
        var foreign = await CreateViaApiAsync(company, hr);
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, used.Invitation.Token, StrongPassword)).StatusCode);   // success: no event
        Assert.Empty(await SecurityEventsAsync(company.CompanyId));
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(company, HttpMethod.Post, $"/api/employees/{replaced.EmployeeId}/invitation", hr.Token)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(company, used.Invitation.Token, StrongPassword + "x")).StatusCode);      // replay
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(company, replaced.Invitation.Token, StrongPassword)).StatusCode);        // replaced
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(other, foreign.Invitation.Token, StrongPassword)).StatusCode);          // wrong tenant
        Clock.Advance(TimeSpan.FromHours(73));
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(company, expired.Invitation.Token, StrongPassword)).StatusCode);        // expired

        var here = await SecurityEventsAsync(company.CompanyId);
        var there = await SecurityEventsAsync(other.CompanyId);
        Assert.Equal(3, here.Count);                                                        // replay, replaced, expired
        Assert.All(here, e => Assert.Equal((SecurityEventTypes.SetPasswordFailed, SecurityEventReasons.InvalidToken, (Guid?)null), (e.EventType, e.Reason, e.UserId)));
        var foreignEvent = Assert.Single(there);                                            // recorded where it was presented, not where it was issued
        Assert.Equal(other.CompanyId, foreignEvent.CompanyId);
        Assert.Null(foreignEvent.UserId);

        // Trying it elsewhere did not burn it.
        Clock.Advance(TimeSpan.FromHours(-73));
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, foreign.Invitation.Token, StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task A_valid_token_of_an_account_that_cannot_be_set_up_names_that_account()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        await using (var db = Fx.CreateDbContext())
        {
            await db.Users.Where(u => u.Id == created.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        var response = await SetPasswordAsync(company, created.Invitation.Token, StrongPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);                       // the same generic answer as for an unknown token
        var entry = Assert.Single(await SecurityEventsAsync(company.CompanyId));
        Assert.Equal(SecurityEventReasons.AccountNotEligible, entry.Reason);
        Assert.Equal(created.UserId, entry.UserId);                                         // known from the token row, i.e. trusted server state
    }

    [Fact]
    public async Task A_successful_set_password_is_audited_and_is_not_a_security_event()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);

        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, created.Invitation.Token, StrongPassword)).StatusCode);

        Assert.Empty(await SecurityEventsAsync(company.CompanyId));
        Assert.Single(await AuditAsync(company.CompanyId, "auth.password_set"));
    }

    [Fact]
    public async Task A_weak_password_is_input_validation_and_neither_burns_the_token_nor_creates_an_event()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);

        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(company, created.Invitation.Token, "short")).StatusCode);

        Assert.Empty(await SecurityEventsAsync(company.CompanyId));
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, created.Invitation.Token, StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task Parallel_attempts_with_one_token_leave_exactly_one_winner_and_one_event_per_loser()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 6).Select(async i =>
        {
            await gate.Task;
            return await SetPasswordAsync(company, created.Invitation.Token, $"Parallel-Password-{i}-Aa9!");
        }).ToArray();
        gate.SetResult();
        var responses = await Task.WhenAll(tasks);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        var events = await SecurityEventsAsync(company.CompanyId);
        Assert.Equal(5, events.Count);
        Assert.All(events, e => Assert.Equal(SecurityEventReasons.InvalidToken, e.Reason));
        Assert.All(events.Where(e => e.UserId is not null), e => Assert.Equal(created.UserId, e.UserId));
        Assert.Single(await AuditAsync(company.CompanyId, "auth.password_set"));
    }

    [Fact]
    public async Task A_failing_audit_entry_rolls_the_change_back_and_records_no_event_because_nothing_was_rejected()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        var hashBefore = (await LoadAsync(created.EmployeeId)).User.PasswordHash;
        Switch.AuditStageFails = true;

        var response = await SetPasswordAsync(company, created.Invitation.Token, StrongPassword);
        Switch.AuditStageFails = false;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(hashBefore, (await LoadAsync(created.EmployeeId)).User.PasswordHash);      // no partial password change
        Assert.Empty(await SecurityEventsAsync(company.CompanyId));
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, created.Invitation.Token, StrongPassword)).StatusCode);   // token intact
    }

    [Fact]
    public async Task A_forwarded_header_from_an_untrusted_peer_cannot_choose_the_recorded_address()
    {
        var (company, _) = await SetupAsync();

        var response = await CallAsync(company, HttpMethod.Post, "/api/auth/set-password", null,
            new { token = AccountInvitationIssuerToken(), newPassword = StrongPassword }, ("X-Forwarded-For", "198.51.100.77"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, Assert.Single(await SecurityEventsAsync(company.CompanyId)).IpAddress);
    }

    // ================= change-password =================

    [Fact]
    public async Task A_wrong_current_password_is_recorded_for_the_authenticated_account_without_any_secret()
    {
        var (company, hr) = await SetupAsync();
        await SetKnownPasswordAsync(hr.UserId, OldPassword, firstLogin: false);
        const string wrong = "Definitely-Wrong-Password-5!";

        var response = await CallAsync(company, HttpMethod.Post, "/api/auth/change-password", hr.Token,
            new { currentPassword = wrong, newPassword = StrongPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var entry = Assert.Single(await SecurityEventsAsync(company.CompanyId));
        Assert.Equal(SecurityEventTypes.ChangePasswordFailed, entry.EventType);
        Assert.Equal(SecurityEventReasons.WrongCurrentPassword, entry.Reason);
        Assert.Equal(hr.UserId, entry.UserId);
        Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, entry.IpAddress);
        Assert.Empty(await AuditAsync(company.CompanyId, "auth.password_changed"));         // nothing changed, so nothing audited as changed

        var text = await EventsTextAsync() + await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(wrong, text);
        Assert.DoesNotContain(StrongPassword, text);
        Assert.DoesNotContain(hr.Token, text);
        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains(wrong) || e.Message.Contains(StrongPassword) || e.Message.Contains(hr.Token));
    }

    [Fact]
    public async Task A_successful_change_is_audited_and_adds_no_event()
    {
        var (company, hr) = await SetupAsync();
        await SetKnownPasswordAsync(hr.UserId, OldPassword, firstLogin: false);

        var response = await CallAsync(company, HttpMethod.Post, "/api/auth/change-password", hr.Token,
            new { currentPassword = OldPassword, newPassword = StrongPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await SecurityEventsAsync(company.CompanyId));
        Assert.Single(await AuditAsync(company.CompanyId, "auth.password_changed"));
    }

    // ================= the table itself =================

    [Fact]
    public async Task Security_events_are_append_only_and_may_only_name_a_real_account()
    {
        var (company, hr) = await SetupAsync();
        SecurityEvent NewEvent(Guid? user) => new()
        {
            Id = Guid.NewGuid(), CompanyId = company.CompanyId, UserId = user, EventType = "test.event", Reason = "test", CreatedAt = DateTime.UtcNow
        };

        await using var db = Fx.CreateDbContext();
        var anonymous = NewEvent(null);
        db.SecurityEvents.AddRange(anonymous, NewEvent(hr.UserId));
        await db.SaveChangesAsync();                                                        // both a nameless and a named event are fine

        anonymous.Reason = "changed";
        var update = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", update.Message);
        db.ChangeTracker.Clear();

        db.SecurityEvents.Remove(await db.SecurityEvents.FirstAsync(e => e.Id == anonymous.Id));
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", delete.Message);
        db.ChangeTracker.Clear();

        db.SecurityEvents.Add(NewEvent(Guid.NewGuid()));                                    // a user that does not exist: referential integrity holds
        var fabricated = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("FK_SecurityEvents_Users_UserId", fabricated.InnerException!.Message);
    }

    // ================= failure behaviour of the writer =================

    private sealed class FakeTenant : ITenantContext
    {
        public Guid CompanyId { get; init; } = Guid.NewGuid();

        public string? Domain => "x.test";

        public bool IsResolved { get; init; } = true;
    }

    private sealed class FakeClient : IClientInfo
    {
        public string? IpAddress => "203.0.113.9";

        public string? UserAgent => "tests";
    }

    private sealed class ListLogger : ILogger<SecurityEventWriter>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task When_the_event_cannot_be_written_the_caller_is_not_affected_and_nothing_secret_is_logged()
    {
        // A context pointing at nothing: connecting fails. (No connection is ever made to a real server - port 1 refuses at once.)
        var unreachable = new DbContextOptionsBuilder<HrmsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=u;Password=secret-pw;Timeout=1;Command Timeout=1")
            .Options;
        var logger = new ListLogger();
        var writer = new SecurityEventWriter(unreachable, new FakeTenant(), new FakeClient(), TimeProvider.System, logger);

        await writer.RecordAsync(new SecurityEventEntry(SecurityEventTypes.SetPasswordFailed, SecurityEventReasons.InvalidToken));   // must not throw

        var logged = Assert.Single(logger.Messages);
        Assert.Contains(SecurityEventTypes.SetPasswordFailed, logged);
        Assert.DoesNotContain("secret-pw", logged);                                          // the connection string never reaches the log
        Assert.DoesNotContain("127.0.0.1", logged);
    }

    [Fact]
    public async Task Without_a_resolved_tenant_nothing_is_written_and_nothing_throws()
    {
        var logger = new ListLogger();
        var writer = new SecurityEventWriter(
            new DbContextOptionsBuilder<HrmsDbContext>().UseNpgsql(Fx.ConnectionString).Options,
            new FakeTenant { IsResolved = false }, new FakeClient(), TimeProvider.System, logger);

        await writer.RecordAsync(new SecurityEventEntry(SecurityEventTypes.SetPasswordFailed, SecurityEventReasons.InvalidToken));

        Assert.Contains("no tenant", Assert.Single(logger.Messages));
    }

    // A well-formed token nobody was ever given.
    private static string AccountInvitationIssuerToken() => HRMS.Application.Features.Auth.AccountInvitationIssuer.NewToken();
}
