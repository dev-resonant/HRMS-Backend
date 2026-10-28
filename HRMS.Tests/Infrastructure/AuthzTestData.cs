using HRMS.API.Authorization;
using HRMS.Application.Common.Exceptions;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HRMS.Tests.Infrastructure;

public sealed record AuthzCompany(Guid CompanyId, string Domain, Guid DepartmentId);

/// <summary>A user (and optionally an employee record) with a ready-to-use bearer token backed by a live session row.</summary>
public sealed record AuthzUser(Guid CompanyId, Guid UserId, Guid? EmployeeId, string Email, string Token);

public static class AuthzTestData
{
    public static Guid SystemRoleId(string key) => SystemRoles.Definitions.Single(d => d.Key == key).Id;

    public static async Task<AuthzCompany> CreateCompanyAsync(PostgresFixture fx)
    {
        await fx.VerifyTargetAsync();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var company = new Company { Id = Guid.NewGuid(), Name = $"Authz Co {suffix}", Domain = $"z-{suffix}.test", LogoUrl = string.Empty, Timezone = "Asia/Kolkata", IsActive = true };
        var department = new Department { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Engineering" };

        await using var db = fx.CreateDbContext();
        db.Companies.Add(company);
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        return new AuthzCompany(company.Id, company.Domain, department.Id);
    }

    /// <summary>A company-scoped (non-system) role with arbitrary permissions JSON.</summary>
    public static async Task<Guid> CreateCompanyRoleAsync(PostgresFixture fx, Guid companyId, string permissionsJson, string name = "Custom")
    {
        await using var db = fx.CreateDbContext();
        var role = new Role { Id = Guid.NewGuid(), CompanyId = companyId, Name = name, DisplayName = name, Permissions = permissionsJson, IsSystem = false };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role.Id;
    }

    public static async Task<AuthzUser> AddUserAsync(
        PostgresFixture fx,
        HrmsWebApplicationFactory factory,
        AuthzCompany company,
        Guid roleId,
        string roleKeyClaim,
        bool withEmployee = true,
        Guid? managerEmployeeId = null,
        bool active = true,
        Guid? employeeCompanyId = null,
        Guid? departmentId = null)
    {
        await fx.VerifyTargetAsync();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var user = new User
        {
            Id = Guid.NewGuid(),
            CompanyId = company.CompanyId,
            RoleId = roleId,
            Name = $"User {suffix}",
            Email = $"u-{suffix}@{company.Domain}",
            PasswordHash = "not-a-real-hash",
            IsActive = active
        };

        Guid? employeeId = null;

        await using (var db = fx.CreateDbContext())
        {
            db.Users.Add(user);

            if (withEmployee)
            {
                var employee = new Employee
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    CompanyId = employeeCompanyId ?? company.CompanyId,
                    DepartmentId = departmentId ?? company.DepartmentId,
                    ManagerId = managerEmployeeId,
                    EmpCode = "E" + suffix,
                    Designation = "Engineer",
                    JoiningDate = new DateOnly(2024, 1, 15)
                };
                db.Employees.Add(employee);
                employeeId = employee.Id;
            }

            await db.SaveChangesAsync();
        }

        var token = TestJwt.Create(fx.JwtSecret, factory.Issuer, factory.Audience, user.Id, company.CompanyId, user.Email, roleKeyClaim);
        await TestData.AddSessionAsync(fx, user.Id, token, DateTime.UtcNow.AddHours(1));

        return new AuthzUser(company.CompanyId, user.Id, employeeId, user.Email, token);
    }

    public static Task<AuthzUser> AddSystemUserAsync(
        PostgresFixture fx,
        HrmsWebApplicationFactory factory,
        AuthzCompany company,
        string roleKey,
        bool withEmployee = true,
        Guid? managerEmployeeId = null,
        bool active = true)
        => AddUserAsync(fx, factory, company, SystemRoleId(roleKey), roleKey, withEmployee, managerEmployeeId, active);
}

/// <summary>
/// Test-only controller that exercises the authorization framework (permission attributes, deny-by-default, scope service,
/// reference validator). It lives in the test assembly and is added to the host only by the authorization tests; the
/// production API has no such endpoints.
/// </summary>
[ApiController]
[Route("api/probe")]
public class AuthzProbeController : ControllerBase
{
    private readonly IDataScopeService _scope;
    private readonly IReferenceValidator _references;
    private readonly ICurrentUser _currentUser;
    private readonly HrmsDbContext _db;

    public AuthzProbeController(IDataScopeService scope, IReferenceValidator references, ICurrentUser currentUser, HrmsDbContext db)
    {
        _scope = scope;
        _references = references;
        _currentUser = currentUser;
        _db = db;
    }

    // GET /employees equivalent: needs team or wider access, so a plain Employee gets 403.
    [HttpGet("list")]
    [RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.ViewTeam)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _scope.ApplyEmployeeReadScope(_db.Employees.AsNoTracking()).Select(e => e.Id).ToListAsync(ct));

    [HttpGet("employee/{id:guid}")]
    [RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.ViewOwn)]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
        => await _scope.CanReadEmployeeAsync(id, ct) ? Ok(new { id }) : throw new ForbiddenException("Out of scope.");

    [HttpGet("edit/{id:guid}")]
    [RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.EditOwn)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
        => await _scope.CanWriteEmployeeAsync(id, ct) ? Ok(new { id }) : throw new ForbiddenException("Out of scope.");

    [HttpGet("context")]
    [RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.ViewOwn)]
    public IActionResult Context() => Ok(new { _currentUser.UserId, _currentUser.CompanyId, _currentUser.RoleKey, _currentUser.EmployeeId });

    // Deliberately declares nothing: the global filter must deny it.
    [HttpGet("undeclared")]
    public IActionResult Undeclared() => Ok();

    [HttpGet("unknown-permission")]
    [RequirePermission("employees", "fly")]
    public IActionResult UnknownPermission() => Ok();

    [HttpGet("anonymous")]
    [AllowAnonymous]
    public IActionResult Anonymous() => Ok();

    [HttpPost("reference")]
    [RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.Create)]
    public async Task<IActionResult> Reference([FromQuery] string kind, [FromQuery] Guid id, CancellationToken ct)
    {
        switch (kind)
        {
            case "employee": await _references.EnsureEmployeeInCompanyAsync(id, "employeeId", ct); break;
            case "department": await _references.EnsureDepartmentInCompanyAsync(id, "departmentId", ct); break;
            case "user": await _references.EnsureUserInCompanyAsync(id, "userId", ct); break;
            case "role": await _references.EnsureRoleAssignableAsync(id, "roleId", ct); break;
            default: return BadRequest();
        }

        return Ok();
    }
}

// ASP0026 is the compiler analyzer for exactly this mistake. The probes below contain the mistake on purpose.
#pragma warning disable ASP0026
/// <summary>REPRODUCES THE DEFECT: controller-level [AllowAnonymous] with action-level permission / self-service declarations.</summary>
[ApiController]
[Route("api/probe-anon-class")]
[AllowAnonymous]
public class AuthzAnonymousClassProbeController : ControllerBase
{
    [HttpGet("permission")]
    [RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.ViewTeam)]
    public IActionResult Permission() => Ok("reached");

    [HttpGet("self")]
    [SelfService]
    public IActionResult Self() => Ok("reached");

    // Declares nothing itself, so it legitimately inherits the controller's anonymous access.
    [HttpGet("plain")]
    public IActionResult Plain() => Ok("reached");
}

/// <summary>The mirror image: controller-level permission with an action-level [AllowAnonymous].</summary>
[ApiController]
[Route("api/probe-permission-class")]
[RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.ViewTeam)]
public class AuthzPermissionClassProbeController : ControllerBase
{
    [HttpGet("anonymous")]
    [AllowAnonymous]
    public IActionResult Anonymous() => Ok("reached");

    // Properly protected through the controller-level declaration.
    [HttpGet("normal")]
    public IActionResult Normal() => Ok("reached");
}

/// <summary>Contradictory declarations that are not about anonymous access.</summary>
[ApiController]
[Route("api/probe-conflict")]
public class AuthzConflictProbeController : ControllerBase
{
    [HttpGet("permission-and-self")]
    [RequirePermission(PermissionNames.Employees.Module, PermissionNames.Employees.ViewOwn)]
    [SelfService]
    public IActionResult PermissionAndSelf() => Ok("reached");

    [HttpGet("bare-authorize-and-anonymous")]
    [Authorize]
    [AllowAnonymous]
    public IActionResult BareAuthorizeAndAnonymous() => Ok("reached");
}

#pragma warning restore ASP0026

public static class ProbeHost
{
    public static Action<IServiceCollection> AddProbeController { get; } =
        services => services.AddControllers().AddApplicationPart(typeof(AuthzProbeController).Assembly);
}
