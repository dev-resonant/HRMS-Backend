using System.Net;
using System.Text.Json;
using HRMS.Application.Interfaces;
using HRMS.Infrastructure.Common;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HRMS.Tests.Employees;

/// <summary>
/// Secure onboarding: a one-time, short-lived, hashed password-setup token instead of a mailed password. Real API, real PostgreSQL;
/// the delivery channel is a recording test double (no provider exists in the product).
/// </summary>
[Collection(PostgresCollection.Name)]
public class AccountOnboardingTests : EmployeeApiTestBase
{
    private static readonly TokenHasher Hasher = new();

    public AccountOnboardingTests(PostgresFixture fx) : base(fx) { }

    private async Task<(AuthzCompany Company, AuthzUser Hr)> SetupAsync()
    {
        var company = await NewCompanyAsync();
        return (company, await HrAsync(company));
    }

    // ================= issuing =================

    [Fact]
    public async Task Creating_an_employee_issues_one_invitation_and_stores_only_its_hash()
    {
        var (company, hr) = await SetupAsync();
        var before = Clock.GetUtcNow().UtcDateTime;

        var created = await CreateViaApiAsync(company, hr);

        var invitation = created.Invitation;
        Assert.Equal(company.CompanyId, invitation.CompanyId);
        Assert.Equal(created.UserId, invitation.UserId);
        Assert.Equal(created.Email, invitation.RecipientEmail);
        Assert.Equal(company.Domain, invitation.CompanyDomain);
        Assert.Equal(43, invitation.Token.Length);                                          // 32 random bytes, base64url
        Assert.Matches("^[A-Za-z0-9_-]{43}$", invitation.Token);
        Assert.InRange(invitation.ExpiresAtUtc, before.AddHours(72).AddSeconds(-1), before.AddHours(72).AddSeconds(1));

        var row = Assert.Single(await TokenRowsAsync(created.UserId));
        Assert.Equal(company.CompanyId, row.CompanyId);
        Assert.Equal(Hasher.Hash(invitation.Token), row.TokenHash);                         // SHA-256 of the token...
        Assert.NotEqual(invitation.Token, row.TokenHash);                                    // ...never the token
        Assert.Equal(invitation.ExpiresAtUtc, row.ExpiresAt, TimeSpan.FromMilliseconds(1));
        Assert.Null(row.ConsumedAt);
        Assert.Null(row.RevokedAt);
        Assert.True((await LoadAsync(created.EmployeeId)).User.IsFirstLogin);
    }

    [Fact]
    public async Task The_token_never_appears_in_the_response_the_audit_trail_or_the_logs()
    {
        var (company, hr) = await SetupAsync();

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId));
        var raw = await response.Content.ReadAsStringAsync();
        var token = Sender.Sent.Single().Token;

        Assert.DoesNotContain(token, raw);
        Assert.DoesNotContain(Hasher.Hash(token), raw);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("X-Invitation-Token"));
        foreach (var audit in await AuditAsync(company.CompanyId))
        {
            Assert.DoesNotContain(token, (audit.OldValue ?? "") + (audit.NewValue ?? ""));
            Assert.DoesNotContain(Hasher.Hash(token), (audit.OldValue ?? "") + (audit.NewValue ?? ""));
        }

        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains(token) || e.Message.Contains(Hasher.Hash(token)));
    }

    [Fact]
    public async Task The_invitation_is_handed_over_only_after_the_data_is_committed()
    {
        var (company, hr) = await SetupAsync();
        bool? visibleToAnotherConnection = null;
        Sender.OnSend = async invitation =>
        {
            await using var db = Fx.CreateDbContext();
            visibleToAnotherConnection = await db.AccountSetupTokens.AnyAsync(t => t.TokenHash == Hasher.Hash(invitation.Token))
                                         && await db.Users.AnyAsync(u => u.Id == invitation.UserId);
        };

        await CreateViaApiAsync(company, hr);

        Assert.True(visibleToAnotherConnection);                 // at delivery time the account and the token hash were already committed
    }

    [Fact]
    public async Task A_rolled_back_creation_delivers_nothing_and_stores_no_token()
    {
        var (company, hr) = await SetupAsync();
        Switch.AuditStageFails = true;

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(Sender.Sent);
        await using var db = Fx.CreateDbContext();
        Assert.Equal(0, await db.AccountSetupTokens.CountAsync(t => t.CompanyId == company.CompanyId));
    }

    [Fact]
    public async Task A_failing_delivery_does_not_undo_the_committed_employee_and_leaks_nothing_into_the_log()
    {
        var (company, hr) = await SetupAsync();
        Sender.ThrowOnSend = true;

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, email: "deliveryfail@example.test"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);                            // the employee exists; the invitation can be re-issued
        var id = (await JsonOf(response)).GetProperty("id").GetGuid();
        Assert.Single(await TokenRowsAsync((await LoadAsync(id)).User.Id));
        Assert.Contains(Logs.Entries, e => e.Message.Contains("failed with InvalidOperationException"));
        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains("deliveryfail@example.test") || e.Message.Contains("simulated"));
    }

    // ================= using the token =================

    [Fact]
    public async Task The_token_sets_the_password_clears_the_flag_consumes_the_token_and_is_audited()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);

        var response = await SetPasswordAsync(company, created.Invitation.Token, StrongPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (user, _) = await LoadAsync(created.EmployeeId);
        Assert.False(user.IsFirstLogin);
        Assert.True(new BCryptPasswordHasher().Verify(StrongPassword, user.PasswordHash));
        var row = Assert.Single(await TokenRowsAsync(created.UserId));
        Assert.NotNull(row.ConsumedAt);

        var audit = Assert.Single(await AuditAsync(company.CompanyId, "auth.password_set"));
        Assert.Equal("auth", audit.Module);
        Assert.Equal(created.UserId, audit.TargetId);
        Assert.Equal(created.UserId, audit.UserId);                                          // the account itself, proven by the token
        Assert.Equal(company.CompanyId, audit.CompanyId);
        Assert.Equal(HrmsWebApplicationFactory.TestRemoteIp, audit.IpAddress);
        using (var after = JsonDocument.Parse(audit.NewValue!))
        {
            Assert.False(after.RootElement.GetProperty("firstLoginPending").GetBoolean());
        }

        var secrets = (audit.OldValue ?? "") + audit.NewValue + await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(StrongPassword, secrets);
        Assert.DoesNotContain(created.Invitation.Token, secrets);
        Assert.DoesNotContain(user.PasswordHash, secrets);
        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains(StrongPassword) || e.Message.Contains(created.Invitation.Token));

        // The account can now log in and is not held back.
        var login = await Factory.ClientFor(company.Domain).PostLoginAsync(created.Email, StrongPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False((await JsonOf(login)).GetProperty("user").GetProperty("passwordChangeRequired").GetBoolean());
    }

    [Fact]
    public async Task A_used_token_cannot_be_used_again()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, created.Invitation.Token, StrongPassword)).StatusCode);
        var hashAfterFirstUse = (await LoadAsync(created.EmployeeId)).User.PasswordHash;

        var second = await SetPasswordAsync(company, created.Invitation.Token, "Another-Strong-Pw-77!");

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal(hashAfterFirstUse, (await LoadAsync(created.EmployeeId)).User.PasswordHash);        // unchanged
        Assert.Single(await AuditAsync(company.CompanyId, "auth.password_set"));
    }

    [Fact]
    public async Task An_expired_token_is_refused_and_a_token_just_inside_its_lifetime_is_accepted()
    {
        var (company, hr) = await SetupAsync();
        var late = await CreateViaApiAsync(company, hr);
        var justInTime = await CreateViaApiAsync(company, hr);

        Clock.Advance(TimeSpan.FromHours(72).Add(TimeSpan.FromSeconds(-30)));
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, justInTime.Invitation.Token, StrongPassword)).StatusCode);

        Clock.Advance(TimeSpan.FromMinutes(1));                                                 // now 30 seconds past the lifetime
        var expired = await SetPasswordAsync(company, late.Invitation.Token, StrongPassword);

        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.True((await LoadAsync(late.EmployeeId)).User.IsFirstLogin);
        Assert.Null(Assert.Single(await TokenRowsAsync(late.UserId)).ConsumedAt);
    }

    [Fact]
    public async Task The_lifetime_follows_the_configuration()
    {
        var (company, hr) = await SetupAsync();
        Options.InvitationLifetime = TimeSpan.FromMinutes(10);
        var created = await CreateViaApiAsync(company, hr);

        Clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(company, created.Invitation.Token, StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task Re_issuing_revokes_the_earlier_invitation_and_only_the_new_one_works()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        Clock.Advance(TimeSpan.FromMinutes(1));                                               // time passes between the two invitations (they are ordered by CreatedAt)

        var reissue = await CallAsync(company, HttpMethod.Post, $"/api/employees/{created.EmployeeId}/invitation", hr.Token);

        Assert.Equal(HttpStatusCode.OK, reissue.StatusCode);
        var body = await JsonOf(reissue);
        Assert.True(body.GetProperty("deliveryConfigured").GetBoolean());
        Assert.DoesNotContain("token", (await reissue.Content.ReadAsStringAsync()), StringComparison.OrdinalIgnoreCase);
        var newToken = Sender.Sent.Last().Token;
        Assert.NotEqual(created.Invitation.Token, newToken);

        var rows = await TokenRowsAsync(created.UserId);
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].RevokedAt);                                                    // the first one
        Assert.Null(rows[1].RevokedAt);

        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(company, created.Invitation.Token, StrongPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, newToken, StrongPassword)).StatusCode);

        var audit = Assert.Single(await AuditAsync(company.CompanyId, "employee.invitation_issued"));
        Assert.Equal(created.EmployeeId, audit.TargetId);
        Assert.Equal(hr.UserId, audit.UserId);
        Assert.Equal(company.CompanyId, audit.CompanyId);
        using var payload = JsonDocument.Parse(audit.NewValue!);
        Assert.Equal(created.UserId, payload.RootElement.GetProperty("userId").GetGuid());
        Assert.Equal(1, payload.RootElement.GetProperty("previousInvitationsRevoked").GetInt32());
        Assert.DoesNotContain(newToken, audit.NewValue);
        Assert.DoesNotContain(Hasher.Hash(newToken), audit.NewValue);
    }

    // ================= tenant and account binding =================

    [Fact]
    public async Task A_token_does_not_work_on_another_companys_host_and_is_not_consumed_by_trying()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var created = await CreateViaApiAsync(company, hr);

        var wrongTenant = await SetPasswordAsync(other, created.Invitation.Token, StrongPassword);

        Assert.Equal(HttpStatusCode.BadRequest, wrongTenant.StatusCode);
        Assert.Null(Assert.Single(await TokenRowsAsync(created.UserId)).ConsumedAt);
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, created.Invitation.Token, StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task The_repository_itself_binds_every_token_and_account_operation_to_the_resolved_tenant()
    {
        // The wrong-host request is also stopped by later layers; this pins the repository layer on its own.
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var created = await CreateViaApiAsync(company, hr);
        var hash = Hasher.Hash(created.Invitation.Token);
        var now = Clock.GetUtcNow().UtcDateTime;
        var tokenId = Assert.Single(await TokenRowsAsync(created.UserId)).Id;

        using (var foreign = Factory.Services.CreateScope())
        {
            ((TenantContext)foreign.ServiceProvider.GetRequiredService<ITenantContext>()).SetTenant(other.CompanyId, other.Domain);
            var repository = foreign.ServiceProvider.GetRequiredService<IAccountRepository>();

            Assert.Null(await repository.FindUsableTokenAsync(hash, now));
            Assert.Null(await repository.GetUserForUpdateAsync(created.UserId));
            Assert.False(await repository.TryConsumeTokenAsync(tokenId, now));
            Assert.Equal(0, await repository.RevokeOpenTokensAsync(created.UserId, now));
        }

        var row = Assert.Single(await TokenRowsAsync(created.UserId));
        Assert.Null(row.ConsumedAt);
        Assert.Null(row.RevokedAt);

        using var own = Factory.Services.CreateScope();
        ((TenantContext)own.ServiceProvider.GetRequiredService<ITenantContext>()).SetTenant(company.CompanyId, company.Domain);
        var ownRepository = own.ServiceProvider.GetRequiredService<IAccountRepository>();
        Assert.NotNull(await ownRepository.FindUsableTokenAsync(hash, now));
        Assert.NotNull(await ownRepository.GetUserForUpdateAsync(created.UserId));
    }

    [Fact]
    public async Task A_token_sets_the_password_of_its_own_account_only()
    {
        var (company, hr) = await SetupAsync();
        var alice = await CreateViaApiAsync(company, hr);
        var bob = await CreateViaApiAsync(company, hr);
        var bobHash = (await LoadAsync(bob.EmployeeId)).User.PasswordHash;

        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, alice.Invitation.Token, StrongPassword)).StatusCode);

        Assert.Equal(bobHash, (await LoadAsync(bob.EmployeeId)).User.PasswordHash);
        Assert.True((await LoadAsync(bob.EmployeeId)).User.IsFirstLogin);
        Assert.False((await LoadAsync(alice.EmployeeId)).User.IsFirstLogin);
    }

    [Fact]
    public async Task The_token_of_a_deactivated_account_is_refused()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        Assert.Equal(HttpStatusCode.OK,
            (await CallAsync(company, HttpMethod.Put, $"/api/employees/{created.EmployeeId}/deactivate", hr.Token, new { lastWorkingDate = "2025-12-31" })).StatusCode);

        var response = await SetPasswordAsync(company, created.Invitation.Token, StrongPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await LoadAsync(created.EmployeeId)).User.IsActive);
    }

    // ================= policy, invalid input, atomicity =================

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("12345678901")]                                // 11 characters
    [InlineData("            ")]                               // blank
    [InlineData("valid-looking-but\u0000-with-nul")]
    [InlineData("valid-looking-but\u0007-with-bell")]
    public async Task A_password_that_breaks_the_policy_is_refused_and_does_not_burn_the_token(string password)
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);

        var response = await SetPasswordAsync(company, created.Invitation.Token, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(Assert.Single(await TokenRowsAsync(created.UserId)).ConsumedAt);
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, created.Invitation.Token, StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task A_password_longer_than_72_bytes_is_refused_because_bcrypt_would_silently_truncate_it()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);

        var exactly72 = new string('a', 72);
        var tooLong = new string('a', 73);
        var multiByte = new string('é', 37);                      // 37 characters = 74 bytes

        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(company, created.Invitation.Token, tooLong)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(company, created.Invitation.Token, multiByte)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, created.Invitation.Token, exactly72)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]       // right shape, never issued
    public async Task Unknown_tokens_are_refused_with_one_generic_answer_and_leave_no_trace(string token)
    {
        var (company, _) = await SetupAsync();
        var before = await CountsAsync(company.CompanyId);

        var response = await SetPasswordAsync(company, token, StrongPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await CountsAsync(company.CompanyId));
        Assert.DoesNotContain("exist", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Expired_used_revoked_and_unknown_tokens_give_the_same_answer()
    {
        var (company, hr) = await SetupAsync();
        var used = await CreateViaApiAsync(company, hr);
        await SetPasswordAsync(company, used.Invitation.Token, StrongPassword);
        var expired = await CreateViaApiAsync(company, hr);
        var revoked = await CreateViaApiAsync(company, hr);
        await CallAsync(company, HttpMethod.Post, $"/api/employees/{revoked.EmployeeId}/invitation", hr.Token);
        Clock.Advance(TimeSpan.FromHours(100));

        var answers = new[]
        {
            await (await SetPasswordAsync(company, used.Invitation.Token, StrongPassword)).Content.ReadAsStringAsync(),
            await (await SetPasswordAsync(company, expired.Invitation.Token, StrongPassword)).Content.ReadAsStringAsync(),
            await (await SetPasswordAsync(company, revoked.Invitation.Token, StrongPassword)).Content.ReadAsStringAsync(),
            await (await SetPasswordAsync(company, "unknown-token-value", StrongPassword)).Content.ReadAsStringAsync()
        };

        Assert.Single(answers.Distinct());
    }

    [Fact]
    public async Task A_failing_audit_entry_rolls_back_the_password_and_does_not_burn_the_token()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        var hashBefore = (await LoadAsync(created.EmployeeId)).User.PasswordHash;
        Switch.AuditStageFails = true;

        var failed = await SetPasswordAsync(company, created.Invitation.Token, StrongPassword);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(hashBefore, (await LoadAsync(created.EmployeeId)).User.PasswordHash);
        Assert.True((await LoadAsync(created.EmployeeId)).User.IsFirstLogin);
        Assert.Null(Assert.Single(await TokenRowsAsync(created.UserId)).ConsumedAt);        // the consumption was rolled back with it

        Switch.AuditStageFails = false;
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, created.Invitation.Token, StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task Setting_the_password_ends_any_existing_session_of_the_account_and_only_that_account()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        await TestData.AddSessionAsync(Fx, created.UserId, "stray-session-token", DateTime.UtcNow.AddHours(1));
        var hrSessions = (await TestData.GetSessionsAsync(Fx, hr.UserId)).Count;

        await SetPasswordAsync(company, created.Invitation.Token, StrongPassword);

        Assert.Empty(await TestData.GetSessionsAsync(Fx, created.UserId));
        Assert.Equal(hrSessions, (await TestData.GetSessionsAsync(Fx, hr.UserId)).Count);
    }

    // ================= concurrency =================

    [Fact]
    public async Task Parallel_attempts_with_the_same_token_succeed_exactly_once()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        using var gate = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            gate.Wait();
            return await SetPasswordAsync(company, created.Invitation.Token, $"Parallel-Strong-Pw-{i}!!");
        })).ToArray();
        gate.Set();
        var responses = await Task.WhenAll(tasks);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        Assert.NotNull(Assert.Single(await TokenRowsAsync(created.UserId)).ConsumedAt);
        Assert.Single(await AuditAsync(company.CompanyId, "auth.password_set"));               // one winner, one audit row
    }

    // ================= who may issue =================

    [Fact]
    public async Task Only_HR_with_the_create_permission_may_issue_and_only_inside_their_company()
    {
        var (company, hr) = await SetupAsync();
        var manager = await ManagerAsync(company);
        var employee = await EmployeeAsync(company);
        var created = await CreateViaApiAsync(company, hr);
        var other = await NewCompanyAsync();
        var otherHr = await HrAsync(other);
        var url = $"/api/employees/{created.EmployeeId}/invitation";
        var sentBefore = Sender.Sent.Count;

        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(company, HttpMethod.Post, url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(company, HttpMethod.Post, url, manager.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(company, HttpMethod.Post, url, employee.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(company, HttpMethod.Post, url, otherHr.Token)).StatusCode);          // wrong tenant host
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(other, HttpMethod.Post, url, otherHr.Token)).StatusCode);              // own host, foreign id
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(company, HttpMethod.Post, $"/api/employees/{Guid.NewGuid()}/invitation", hr.Token)).StatusCode);
        Assert.Equal(sentBefore, Sender.Sent.Count);
        Assert.Single(await TokenRowsAsync(created.UserId));
    }

    [Fact]
    public async Task An_invitation_cannot_be_issued_to_an_account_that_is_already_set_up_or_deactivated()
    {
        var (company, hr) = await SetupAsync();
        var active = await CreateViaApiAsync(company, hr);
        await SetPasswordAsync(company, active.Invitation.Token, StrongPassword);
        var gone = await CreateViaApiAsync(company, hr);
        await CallAsync(company, HttpMethod.Put, $"/api/employees/{gone.EmployeeId}/deactivate", hr.Token, new { lastWorkingDate = "2025-12-31" });
        var sentBefore = Sender.Sent.Count;

        var alreadySetUp = await CallAsync(company, HttpMethod.Post, $"/api/employees/{active.EmployeeId}/invitation", hr.Token);
        var deactivated = await CallAsync(company, HttpMethod.Post, $"/api/employees/{gone.EmployeeId}/invitation", hr.Token);

        Assert.Equal(HttpStatusCode.Conflict, alreadySetUp.StatusCode);                         // no way to take over a live account
        Assert.Equal(HttpStatusCode.Conflict, deactivated.StatusCode);
        Assert.Equal(sentBefore, Sender.Sent.Count);
    }

    [Fact]
    public async Task A_failed_re_issue_keeps_the_earlier_invitation_usable()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        Switch.AuditStageFails = true;

        var failed = await CallAsync(company, HttpMethod.Post, $"/api/employees/{created.EmployeeId}/invitation", hr.Token);
        Switch.AuditStageFails = false;

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        var row = Assert.Single(await TokenRowsAsync(created.UserId));                          // no second token, and the first was not revoked
        Assert.Null(row.RevokedAt);
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, created.Invitation.Token, StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task Without_a_delivery_channel_the_invitation_exists_but_the_response_says_nothing_was_sent()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        Sender.IsConfigured = false;

        var response = await CallAsync(company, HttpMethod.Post, $"/api/employees/{created.EmployeeId}/invitation", hr.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await JsonOf(response)).GetProperty("deliveryConfigured").GetBoolean());
        Assert.Contains(Logs.Entries, e => e.Message.Contains("no invitation delivery channel is configured"));
        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains(Sender.Sent.Last().Token));
    }
}
