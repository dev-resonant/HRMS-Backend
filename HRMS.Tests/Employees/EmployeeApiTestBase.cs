using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HRMS.API.RateLimiting;
using HRMS.Application.Audit;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Domain.Entities;
using HRMS.Domain.Enums;
using HRMS.Infrastructure.Outbox;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HRMS.Tests.Employees;

public sealed record SeededEmployee(Guid UserId, Guid EmployeeId, string Email, string Name, string EmpCode, string Token);

/// <summary>Flags the tests can flip to make a collaborator fail, to prove atomic rollback against the real database.</summary>
public sealed class FailureSwitch
{
    public volatile bool AuditStageFails;

    public string? FixedEmployeeCode { get; set; }
}

public sealed class SwitchableAuditWriter : IAuditWriter
{
    private readonly HRMS.Infrastructure.Audit.AuditWriter _inner;
    private readonly FailureSwitch _switch;

    public SwitchableAuditWriter(HRMS.Infrastructure.Audit.AuditWriter inner, FailureSwitch failureSwitch)
    {
        _inner = inner;
        _switch = failureSwitch;
    }

    public void Stage(AuditEntry entry)
    {
        if (_switch.AuditStageFails)
        {
            throw new InvalidOperationException("simulated audit failure with secret detail xyz");
        }

        _inner.Stage(entry);
    }

    public Task RecordFailedLoginAsync(Guid userId, Guid companyId, CancellationToken cancellationToken = default)
        => _inner.RecordFailedLoginAsync(userId, companyId, cancellationToken);
}

public sealed class SwitchableCodeGenerator : IEmployeeCodeGenerator
{
    private readonly HRMS.Infrastructure.Employees.EmployeeCodeGenerator _inner;
    private readonly FailureSwitch _switch;

    public SwitchableCodeGenerator(HRMS.Infrastructure.Employees.EmployeeCodeGenerator inner, FailureSwitch failureSwitch)
    {
        _inner = inner;
        _switch = failureSwitch;
    }

    // When a fixed code is set the real generator is bypassed, which is exactly the situation the unique index guards against.
    public Task<string> NextAsync(CancellationToken cancellationToken = default)
        => _switch.FixedEmployeeCode is { } fixedCode ? Task.FromResult(fixedCode) : _inner.NextAsync(cancellationToken);
}

/// <summary>Test delivery channel: records what the production code hands over, after the commit. No network, no provider.</summary>
public sealed class RecordingInvitationSender : IAccountInvitationSender
{
    private readonly List<AccountInvitation> _sent = new();

    public bool IsConfigured { get; set; } = true;

    public bool ThrowOnSend { get; set; }

    /// <summary>Runs inside SendAsync, for assertions about the state at the moment of delivery (for example: is the data committed?).</summary>
    public Func<AccountInvitation, Task>? OnSend { get; set; }

    public IReadOnlyList<AccountInvitation> Sent { get { lock (_sent) return _sent.ToList(); } }

    public async Task SendAsync(AccountInvitation invitation, CancellationToken cancellationToken)
    {
        if (OnSend is not null)
        {
            await OnSend(invitation);
        }

        if (ThrowOnSend)
        {
            throw new InvalidOperationException("simulated delivery failure for " + invitation.RecipientEmail);
        }

        lock (_sent)
        {
            _sent.Add(invitation);
        }
    }
}

/// <summary>A clock the tests can move forward (only the onboarding code uses the injected TimeProvider).</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public sealed class FixedPasswordGenerator : ITemporaryPasswordGenerator
{
    public const string Value = "Fixed-Temp-Pw-9XyZ-4471";

    public string Generate() => Value;
}

/// <summary>Real API, real PostgreSQL (guarded Testcontainers), real pipeline: helpers shared by the employee tests.</summary>
public abstract class EmployeeApiTestBase : IDisposable
{
    protected EmployeeApiTestBase(PostgresFixture fx, bool fixedPassword = false, IReadOnlyDictionary<string, string?>? settings = null)
    {
        Fx = fx;
        Switch = new FailureSwitch();
        Logs = new CapturingLoggerProvider();
        Sender = new RecordingInvitationSender();
        Clock = new TestClock();
        Options = new AccountSetupOptions();

        Factory = new HrmsWebApplicationFactory(fx, services =>
        {
            services.AddSingleton<ILoggerProvider>(Logs);
            services.AddSingleton(Switch);
            services.AddSingleton<IAccountInvitationSender>(Sender);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton(Options);

            // Wrap the real implementations so tests can make them fail; behaviour is unchanged while the switches are off.
            services.AddScoped<HRMS.Infrastructure.Employees.EmployeeCodeGenerator>();
            services.AddScoped<IEmployeeCodeGenerator, SwitchableCodeGenerator>();
            services.AddScoped<IAuditWriter>(sp => new SwitchableAuditWriter(sp.GetRequiredService<HRMS.Infrastructure.Audit.AuditWriter>(), Switch));

            if (fixedPassword)
            {
                services.AddSingleton<ITemporaryPasswordGenerator, FixedPasswordGenerator>();
            }
        }, settings: settings);
        Factory.AssertUsesFixtureDatabase();
    }

    /// <summary>The host's live outbox settings (mutate before the first request that should see the change).</summary>
    protected OutboxOptions OutboxSettings => Factory.Services.GetRequiredService<OutboxOptions>();

    /// <summary>The host's live rate-limit settings.</summary>
    protected RateLimitingOptions Limits => Factory.Services.GetRequiredService<RateLimitingOptions>();

    /// <summary>Runs one outbox batch exactly as the background worker would, in a scope of its own.</summary>
    protected async Task<int> ProcessOutboxAsync()
    {
        using var scope = Factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<InvitationOutboxProcessor>().ProcessDueAsync(CancellationToken.None);
    }

    protected async Task<List<OutboxMessage>> OutboxAsync(Guid companyId)
    {
        await using var db = Fx.CreateDbContext();
        return await db.OutboxMessages.AsNoTracking().Where(m => m.CompanyId == companyId).OrderBy(m => m.CreatedAt).ToListAsync();
    }

    protected async Task<List<SecurityEvent>> SecurityEventsAsync(Guid companyId, string? eventType = null)
    {
        await using var db = Fx.CreateDbContext();
        return await db.SecurityEvents.AsNoTracking()
            .Where(e => e.CompanyId == companyId && (eventType == null || e.EventType == eventType))
            .OrderBy(e => e.CreatedAt).ToListAsync();
    }

    protected PostgresFixture Fx { get; }

    protected HrmsWebApplicationFactory Factory { get; }

    protected FailureSwitch Switch { get; }

    protected CapturingLoggerProvider Logs { get; }

    protected RecordingInvitationSender Sender { get; }

    protected TestClock Clock { get; }

    protected AccountSetupOptions Options { get; }

    public void Dispose() => Factory.Dispose();

    protected Task<AuthzCompany> NewCompanyAsync() => AuthzTestData.CreateCompanyAsync(Fx);

    protected async Task<Guid> AddDepartmentAsync(Guid companyId, string name = "Finance")
    {
        await using var db = Fx.CreateDbContext();
        var department = new Department { Id = Guid.NewGuid(), CompanyId = companyId, Name = name };
        db.Departments.Add(department);
        await db.SaveChangesAsync();
        return department.Id;
    }

    protected Task<AuthzUser> HrAsync(AuthzCompany company) => AuthzTestData.AddSystemUserAsync(Fx, Factory, company, SystemRoleKeys.HrAdmin);

    protected Task<AuthzUser> ManagerAsync(AuthzCompany company) => AuthzTestData.AddSystemUserAsync(Fx, Factory, company, SystemRoleKeys.Manager);

    protected Task<AuthzUser> EmployeeAsync(AuthzCompany company, Guid? managerEmployeeId = null)
        => AuthzTestData.AddSystemUserAsync(Fx, Factory, company, SystemRoleKeys.Employee, managerEmployeeId: managerEmployeeId);

    /// <summary>Inserts a login account + employee with exact values (no API involved) and gives it a ready bearer token.</summary>
    protected async Task<SeededEmployee> SeedAsync(
        AuthzCompany company,
        string name,
        string empCode,
        string roleKey = SystemRoleKeys.Employee,
        string designation = "Engineer",
        EmployeeStatus status = EmployeeStatus.Active,
        DateOnly? joiningDate = null,
        Guid? managerEmployeeId = null,
        Guid? departmentId = null,
        string? email = null,
        bool userActive = true)
    {
        await Fx.VerifyTargetAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User
        {
            Id = Guid.NewGuid(),
            CompanyId = company.CompanyId,
            RoleId = AuthzTestData.SystemRoleId(roleKey),
            Name = name,
            Email = email ?? $"s-{suffix}@{company.Domain}",
            PasswordHash = "not-a-real-hash",
            IsActive = userActive
        };
        var employee = new Employee
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CompanyId = company.CompanyId,
            DepartmentId = departmentId ?? company.DepartmentId,
            ManagerId = managerEmployeeId,
            EmpCode = empCode,
            Designation = designation,
            JoiningDate = joiningDate ?? new DateOnly(2024, 1, 15),
            Status = status
        };

        await using (var db = Fx.CreateDbContext())
        {
            db.Users.Add(user);
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
        }

        var token = TestJwt.Create(Fx.JwtSecret, Factory.Issuer, Factory.Audience, user.Id, company.CompanyId, user.Email, roleKey);
        await TestData.AddSessionAsync(Fx, user.Id, token, DateTime.UtcNow.AddHours(1));

        return new SeededEmployee(user.Id, employee.Id, user.Email, name, empCode, token);
    }

    /// <summary>Inserts many accounts + employees quickly (no tokens). Codes are EMP-0001... starting at <paramref name="firstNumber"/>.</summary>
    protected async Task<List<Guid>> SeedManyAsync(AuthzCompany company, int count, int firstNumber = 1, string namePrefix = "Person", EmployeeStatus status = EmployeeStatus.Active, Guid? departmentId = null)
    {
        await Fx.VerifyTargetAsync();

        var ids = new List<Guid>();
        await using var db = Fx.CreateDbContext();

        for (var i = 0; i < count; i++)
        {
            var number = firstNumber + i;
            var user = new User
            {
                Id = Guid.NewGuid(),
                CompanyId = company.CompanyId,
                RoleId = AuthzTestData.SystemRoleId(SystemRoleKeys.Employee),
                Name = $"{namePrefix} {number:D3}",
                Email = $"{namePrefix.ToLowerInvariant()}{number}-{Guid.NewGuid().ToString("N")[..6]}@{company.Domain}",
                PasswordHash = "not-a-real-hash",
                IsActive = status != EmployeeStatus.Inactive
            };
            var employee = new Employee
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                CompanyId = company.CompanyId,
                DepartmentId = departmentId ?? company.DepartmentId,
                EmpCode = $"EMP-{number:D4}",
                Designation = "Engineer",
                JoiningDate = new DateOnly(2024, 1, 1).AddDays(number),
                Status = status
            };
            db.Users.Add(user);
            db.Employees.Add(employee);
            ids.Add(employee.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    public const string StrongPassword = "Correct-Horse-Battery-9!";

    /// <summary>Creates an employee through the real API as HR and returns the invitation the delivery channel received after the commit.</summary>
    protected async Task<(Guid EmployeeId, Guid UserId, string Email, AccountInvitation Invitation)> CreateViaApiAsync(AuthzCompany company, AuthzUser hr, string? email = null)
    {
        email ??= $"new-{Guid.NewGuid():N}@example.test";
        var before = Sender.Sent.Count;

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateBody(company.DepartmentId, email: email));
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);

        var employeeId = (await JsonOf(response)).GetProperty("id").GetGuid();
        var (user, _) = await LoadAsync(employeeId);
        var invitation = Sender.Sent.Skip(before).Single(i => i.UserId == user.Id);

        return (employeeId, user.Id, user.Email, invitation);
    }

    protected Task<HttpResponseMessage> SetPasswordAsync(AuthzCompany company, string token, string newPassword)
        => CallAsync(company, HttpMethod.Post, "/api/auth/set-password", null, new { token, newPassword });

    protected async Task<List<AccountSetupToken>> TokenRowsAsync(Guid userId)
    {
        await using var db = Fx.CreateDbContext();
        return await db.AccountSetupTokens.AsNoTracking().Where(t => t.UserId == userId).OrderBy(t => t.CreatedAt).ToListAsync();
    }

    /// <summary>Gives an existing account a password the test knows and sets its first-login flag.</summary>
    protected async Task SetKnownPasswordAsync(Guid userId, string password, bool firstLogin)
    {
        var hash = TestData.HashPassword(password);

        await using var db = Fx.CreateDbContext();
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, hash).SetProperty(u => u.IsFirstLogin, firstLogin));
    }

    protected async Task SetFirstLoginAsync(Guid userId, bool firstLogin)
    {
        await using var db = Fx.CreateDbContext();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsFirstLogin, firstLogin));
    }

    protected async Task<HttpResponseMessage> CallAsync(AuthzCompany company, HttpMethod method, string url, string? token, object? body = null, params (string Name, string Value)[] headers)
    {
        var client = Factory.ClientFor(company.Domain);
        using var request = new HttpRequestMessage(method, url);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    protected Task<HttpResponseMessage> GetAsync(AuthzCompany company, string url, string? token) => CallAsync(company, HttpMethod.Get, url, token);

    protected static async Task<JsonElement> JsonOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>());

    protected static object CreateBody(
        Guid departmentId,
        string name = "Asha Rao",
        string? email = null,
        Guid? managerId = null,
        string designation = "Software Engineer",
        string joiningDate = "2024-03-01")
        => new
        {
            name,
            email = email ?? $"asha-{Guid.NewGuid().ToString("N")[..8]}@example.test",
            departmentId,
            managerId,
            designation,
            joiningDate
        };

    protected async Task<List<AuditLog>> AuditAsync(Guid companyId, string? action = null)
    {
        await using var db = Fx.CreateDbContext();
        var query = db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId);

        if (action is not null)
        {
            query = query.Where(a => a.Action == action);
        }

        return await query.OrderBy(a => a.CreatedAt).ToListAsync();
    }

    protected async Task<(int Users, int Employees, int Audit)> CountsAsync(Guid companyId)
    {
        await using var db = Fx.CreateDbContext();
        return (
            await db.Users.CountAsync(u => u.CompanyId == companyId),
            await db.Employees.CountAsync(e => e.CompanyId == companyId),
            await db.AuditLogs.CountAsync(a => a.CompanyId == companyId));
    }

    protected async Task<(User User, Employee Employee)> LoadAsync(Guid employeeId)
    {
        await using var db = Fx.CreateDbContext();
        var employee = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == employee.UserId);
        return (user, employee);
    }
}
