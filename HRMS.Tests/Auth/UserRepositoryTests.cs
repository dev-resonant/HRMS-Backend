using HRMS.Infrastructure.Persistence.Repositories;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Tests.Auth;

[Collection(PostgresCollection.Name)]
public class UserRepositoryTests
{
    private readonly PostgresFixture _fx;

    public UserRepositoryTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task UpdateLastLoginAsync_sets_only_LastLogin_for_the_matching_user_and_company()
    {
        var tenant = await TestData.CreateTenantAsync(_fx);
        var neighbour = await TestData.CreateTenantAsync(_fx);
        var stamp = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        await using var db = _fx.CreateDbContext();
        var repository = new UserRepository(db);

        await repository.UpdateLastLoginAsync(tenant.UserId, tenant.CompanyId, stamp);

        await using var verify = _fx.CreateDbContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == tenant.UserId);
        var other = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == neighbour.UserId);

        Assert.Equal(stamp, user.LastLogin);
        Assert.True(user.IsActive);
        Assert.Null(other.LastLogin);
    }

    [Fact]
    public async Task UpdateLastLoginAsync_is_company_scoped_and_ignores_a_user_id_from_another_company()
    {
        var tenant = await TestData.CreateTenantAsync(_fx);
        var otherCompany = await TestData.CreateTenantAsync(_fx);

        await using var db = _fx.CreateDbContext();
        var repository = new UserRepository(db);

        // right user id, wrong company id: must change nothing
        await repository.UpdateLastLoginAsync(tenant.UserId, otherCompany.CompanyId, DateTime.UtcNow);

        await using var verify = _fx.CreateDbContext();
        var user = await verify.Users.AsNoTracking().SingleAsync(u => u.Id == tenant.UserId);
        Assert.Null(user.LastLogin);
    }
}
