using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using HRMS.API.Controllers;
using HRMS.Application.Features.Auth;
using HRMS.Application.Features.Auth.Commands.ChangePassword;
using HRMS.Application.Features.Auth.Commands.Login;
using HRMS.Application.Features.Auth.Commands.SetPassword;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Common;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Tests.Employees;

/// <summary>Replay, expiry, tenant binding, first-login and secret-exposure review of the password-setup token and the password endpoints.</summary>
[Collection(PostgresCollection.Name)]
public class TokenLifecycleTests : EmployeeApiTestBase
{
    public TokenLifecycleTests(PostgresFixture fx) : base(fx, settings: new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = "http://localhost:5173" }) { }

    private async Task<(AuthzCompany Company, AuthzUser Hr)> SetupAsync()
    {
        var company = await NewCompanyAsync();
        return (company, await HrAsync(company));
    }

    // ================= replay, expiry, first login =================

    [Fact]
    public async Task Setting_the_password_does_not_log_anybody_in_and_returns_no_credential()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);

        var response = await SetPasswordAsync(company, created.Invitation.Token, StrongPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "message" }, body.RootElement.EnumerateObject().Select(p => p.Name));       // no JWT, no session id, nothing else
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await using var db = Fx.CreateDbContext();
        Assert.Equal(0, await db.Sessions.CountAsync(s => s.UserId == created.UserId));                // a separate, explicit login is still required
    }

    [Fact]
    public async Task An_account_that_already_has_a_password_refuses_a_leftover_token_and_keeps_its_password()
    {
        var (company, hr) = await SetupAsync();
        var created = await CreateViaApiAsync(company, hr);
        await SetKnownPasswordAsync(created.UserId, "Existing-Password-Value-7!", firstLogin: false);   // e.g. it changed its password another way
        var hashBefore = (await LoadAsync(created.EmployeeId)).User.PasswordHash;

        var response = await SetPasswordAsync(company, created.Invitation.Token, StrongPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(hashBefore, (await LoadAsync(created.EmployeeId)).User.PasswordHash);
        Assert.Null(Assert.Single(await TokenRowsAsync(created.UserId)).ConsumedAt);                    // refused before the token was spent
        Assert.Equal(SecurityEventReasons.AccountNotEligible, Assert.Single(await SecurityEventsAsync(company.CompanyId)).Reason);
    }

    [Fact]
    public async Task A_token_that_was_replaced_by_the_delivery_worker_is_the_only_one_that_ever_worked()
    {
        var (company, hr) = await SetupAsync();
        OutboxSettings.InlineDelivery = false;
        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId));
        var userId = (await LoadAsync((await JsonOf(response)).GetProperty("id").GetGuid())).User.Id;
        var placeholderHash = Assert.Single(await TokenRowsAsync(userId)).TokenHash;

        await ProcessOutboxAsync();

        var delivered = Assert.Single(Sender.Sent);
        var after = Assert.Single(await TokenRowsAsync(userId));
        Assert.NotEqual(placeholderHash, after.TokenHash);                                              // the value stored at creation was replaced...
        Assert.Equal(new TokenHasher().Hash(delivered.Token), after.TokenHash);                        // ...by the hash of what was mailed
        Assert.Equal(HttpStatusCode.OK, (await SetPasswordAsync(company, delivered.Token, StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task A_token_just_past_its_expiry_fails_at_the_boundary_and_a_delayed_delivery_does_not_extend_it()
    {
        var (company, hr) = await SetupAsync();
        OutboxSettings.InlineDelivery = false;
        Options.InvitationLifetime = TimeSpan.FromHours(1);
        await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId));

        Clock.Advance(TimeSpan.FromMinutes(59));                                                         // delivered late, one minute before expiry
        await ProcessOutboxAsync();
        var invitation = Assert.Single(Sender.Sent);
        Assert.Equal(Clock.GetUtcNow().UtcDateTime.AddMinutes(1), invitation.ExpiresAtUtc, TimeSpan.FromSeconds(1));   // the promised moment, not later

        Clock.Advance(TimeSpan.FromMinutes(1));                                                          // exactly at the expiry instant

        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(company, invitation.Token, StrongPassword)).StatusCode);
    }

    // ================= secrets in strings, logs, contracts =================

    [Fact]
    public void The_commands_that_carry_secrets_print_only_their_name()
    {
        var set = new SetPasswordCommand("setup-token-VALUE", "new-password-VALUE");
        var change = new ChangePasswordCommand("jwt-VALUE", "current-password-VALUE", "new-password-VALUE");

        foreach (var text in new[] { set.ToString(), change.ToString(), $"{set}", $"{change}", set.ToString() + change })
        {
            Assert.DoesNotContain("VALUE", text);
        }

        Assert.Equal("SetPasswordCommand", set.ToString());
        Assert.Equal("ChangePasswordCommand", change.ToString());
    }

    [Fact]
    public void No_record_with_a_secret_looking_member_uses_the_compiler_generated_ToString()
    {
        var secretName = new Regex("password|token|secret|credential|hash", RegexOptions.IgnoreCase);
        var assemblies = new[] { typeof(SetPasswordCommand).Assembly, typeof(User).Assembly, typeof(AuthController).Assembly };

        var offenders = assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null)       // = a record
            .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.PropertyType == typeof(string) && secretName.IsMatch(p.Name)))
            .Where(t =>
            {
                var toString = t.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!;
                return toString.DeclaringType != t || toString.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null;
            })
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_login_and_request_objects_are_classes_that_print_no_member()
    {
        // Plain classes print only their type name, so none of these can leak a value through string formatting or a log template.
        var login = new LoginCommand { Email = "a@b.test", Password = "pw-VALUE" };
        var changeRequest = new ChangePasswordRequest { CurrentPassword = "cur-VALUE", NewPassword = "new-VALUE" };
        var setRequest = new SetPasswordRequest { Token = "tok-VALUE", NewPassword = "new-VALUE" };
        var response = new LoginResponseDto { Token = "jwt-VALUE" };

        foreach (var text in new[] { login.ToString(), changeRequest.ToString(), setRequest.ToString(), response.ToString() })
        {
            Assert.DoesNotContain("VALUE", text);
            Assert.DoesNotContain("@", text);
        }
    }

    [Fact]
    public async Task Failed_requests_leak_neither_the_submitted_token_nor_the_password_into_the_response_or_the_logs()
    {
        var (company, _) = await SetupAsync();
        var token = AccountInvitationIssuer.NewToken();
        const string password = "Submitted-Password-Value-8!";

        var unknown = await SetPasswordAsync(company, token, password);
        var weak = await SetPasswordAsync(company, token, "short-VALUE");
        var malformed = await CallAsync(company, HttpMethod.Post, "/api/auth/set-password", null, new { token = new string('x', 5000), newPassword = password });

        foreach (var response in new[] { unknown, weak, malformed })
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(token, text);
            Assert.DoesNotContain(password, text);
        }

        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains(token) || e.Message.Contains(password) || e.Message.Contains("short-VALUE"));
    }

    [Fact]
    public async Task The_swagger_contract_documents_the_throttled_endpoints_and_publishes_no_example_secrets()
    {
        var (company, _) = await SetupAsync();

        var response = await GetAsync(company, "/swagger/v1/swagger.json", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        foreach (var (path, method) in new[]
        {
            ("/api/auth/login", "post"), ("/api/auth/set-password", "post"),
            ("/api/auth/change-password", "post"), ("/api/employees/{id}/invitation", "post")
        })
        {
            Assert.True(paths.GetProperty(path).GetProperty(method).GetProperty("responses").TryGetProperty("429", out _), $"{method} {path} must document 429");
        }

        // No example/default values on the secret-carrying schemas (an example would be a value people copy into real requests).
        foreach (var name in new[] { "SetPasswordRequest", "ChangePasswordRequest", "LoginCommand" })
        {
            var properties = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name).GetProperty("properties");

            foreach (var property in properties.EnumerateObject())
            {
                Assert.False(property.Value.TryGetProperty("example", out _), $"{name}.{property.Name} must not carry an example");
                Assert.False(property.Value.TryGetProperty("default", out var def) && def.ValueKind == JsonValueKind.String && def.GetString()!.Length > 0, $"{name}.{property.Name} must not carry a default");
            }
        }

        // The invitation endpoint never promises a token in its response.
        var raw = await (await GetAsync(company, "/swagger/v1/swagger.json", null)).Content.ReadAsStringAsync();
        var invitationSchema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("InvitationIssuedResponse").GetRawText();
        Assert.DoesNotContain("token", invitationSchema, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrEmpty(raw));
    }

    [Fact]
    public async Task The_etag_header_is_exposed_to_browsers_by_the_cors_policy()
    {
        var (company, _) = await SetupAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/employees");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        var client = Factory.ClientFor(company.Domain);

        var response = await client.SendAsync(request);                                                  // 401: the CORS headers are added regardless

        Assert.Equal("http://localhost:5173", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("ETag", Assert.Single(response.Headers.GetValues("Access-Control-Expose-Headers")));
    }
}
