using System.Reflection;
using System.Runtime.ExceptionServices;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence.Repositories;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HRMS.Tests.Auth;

/// <summary>
/// Regression tests for audit defect D1: the login's LastLogin update used to write back every column it had read
/// (User and Role) and could revert concurrent changes to IsActive, PasswordHash and role permissions.
/// </summary>
[Collection(PostgresCollection.Name)]
public class LoginDataIntegrityTests
{
    private readonly PostgresFixture _fx;

    public LoginDataIntegrityTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Login_does_not_overwrite_concurrent_changes_to_user_or_role()
    {
        var tenant = await TestData.CreateTenantAsync(_fx);
        var changedHash = TestData.HashPassword(TestData.NewPassword());
        const string changedPermissions = "{\"changed\": [\"yes\"]}";
        var hookRan = false;

        // Simulates another request committing changes AFTER the login handler has read the user and BEFORE it writes.
        async Task ConcurrentChange()
        {
            hookRan = true;
            await _fx.VerifyTargetAsync();
            await using var db = _fx.CreateDbContext();

            await db.Users.Where(u => u.Id == tenant.UserId).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IsActive, false)
                .SetProperty(u => u.PasswordHash, changedHash));

            await db.Roles.Where(r => r.Id == tenant.RoleId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Permissions, changedPermissions));
        }

        using var factory = new HrmsWebApplicationFactory(_fx, services =>
        {
            services.RemoveAll<IUserRepository>();
            services.AddScoped<IUserRepository>(sp =>
                HookingProxy.Create<IUserRepository>(ActivatorUtilities.CreateInstance<UserRepository>(sp), ConcurrentChange));
        });
        factory.AssertUsesFixtureDatabase();

        var client = factory.ClientFor(tenant.Domain);

        // The handler works from the stale read (active user, original password), so this login succeeds.
        var login = await client.LoginAsync(tenant.Email, tenant.Password);

        Assert.True(hookRan, "The concurrent change did not run, so the test proved nothing.");
        Assert.False(string.IsNullOrWhiteSpace(login.Token));

        await using var verify = _fx.CreateDbContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == tenant.UserId);
        var role = await verify.Roles.AsNoTracking().SingleAsync(r => r.Id == tenant.RoleId);

        Assert.False(user.IsActive);                                   // deactivation must not be reverted by the login
        Assert.Equal(changedHash, user.PasswordHash);                  // a concurrent password change must survive
        Assert.Contains("changed", role.Permissions);                  // concurrent role-permission edit must survive
        Assert.NotNull(user.LastLogin);                                // and the login itself must still be recorded
    }

    [Fact]
    public async Task Login_changes_only_LastLogin_on_the_user_and_nothing_on_the_role()
    {
        var tenant = await TestData.CreateTenantAsync(_fx, permissionsJson: "{\"employees\": [\"view_own\"]}");

        User before;
        Role roleBefore;
        await using (var db = _fx.CreateDbContext())
        {
            before = await db.Users.AsNoTracking().SingleAsync(u => u.Id == tenant.UserId);
            roleBefore = await db.Roles.AsNoTracking().SingleAsync(r => r.Id == tenant.RoleId);
        }

        Assert.Null(before.LastLogin);

        using var factory = new HrmsWebApplicationFactory(_fx);
        factory.AssertUsesFixtureDatabase();
        await factory.ClientFor(tenant.Domain).LoginAsync(tenant.Email, tenant.Password);

        await using var verify = _fx.CreateDbContext();
        var after = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == tenant.UserId);
        var roleAfter = await verify.Roles.AsNoTracking().SingleAsync(r => r.Id == tenant.RoleId);

        Assert.NotNull(after.LastLogin);
        Assert.InRange((DateTime.UtcNow - after.LastLogin!.Value).TotalMinutes, -1, 1);

        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.IsActive, after.IsActive);
        Assert.Equal(before.IsFirstLogin, after.IsFirstLogin);
        Assert.Equal(before.RoleId, after.RoleId);
        Assert.Equal(before.CompanyId, after.CompanyId);
        Assert.Equal(before.CreatedAt, after.CreatedAt);

        Assert.Equal(roleBefore.Name, roleAfter.Name);
        Assert.Equal(roleBefore.DisplayName, roleAfter.DisplayName);
        Assert.Equal(roleBefore.Permissions, roleAfter.Permissions);
        Assert.Equal(roleBefore.IsSystem, roleAfter.IsSystem);
        Assert.Equal(roleBefore.CompanyId, roleAfter.CompanyId);
    }
}

/// <summary>
/// Reflection-based decorator: forwards every call to the real repository and, after GetByEmailAsync completes,
/// runs a hook. It does not reference any member of the interface, so it compiles before and after the D1 fix.
/// </summary>
public class HookingProxy : DispatchProxy
{
    private object _inner = null!;
    private Func<Task> _afterGetByEmail = null!;

    public static T Create<T>(T inner, Func<Task> afterGetByEmail) where T : class
    {
        var proxy = Create<T, HookingProxy>();
        var hooking = (HookingProxy)(object)proxy;
        hooking._inner = inner;
        hooking._afterGetByEmail = afterGetByEmail;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        try
        {
            var result = targetMethod!.Invoke(_inner, args);

            if (targetMethod.Name == "GetByEmailAsync" && result is Task<User?> task)
            {
                return RunHookAfter(task);
            }

            return result;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private async Task<User?> RunHookAfter(Task<User?> task)
    {
        var user = await task;
        await _afterGetByEmail();
        return user;
    }
}
