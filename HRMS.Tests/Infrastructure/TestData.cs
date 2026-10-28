using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace HRMS.Tests.Infrastructure;

public sealed record TestTenant(
    Guid CompanyId,
    string Domain,
    Guid RoleId,
    Guid UserId,
    string Email,
    string Password);

public static class TestData
{
    private static readonly BCryptPasswordHasher Hasher = new();
    private static readonly TokenHasher TokenHasher = new();

    /// <summary>Random, test-only password. Not a real credential.</summary>
    public static string NewPassword() => "Pw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) + "!a1";

    public static string HashPassword(string password) => Hasher.Hash(password);

    public static string HashToken(string token) => TokenHasher.Hash(token);

    /// <summary>Creates a company (unique domain), an "Admin" role and one user, directly in the disposable database.</summary>
    public static async Task<TestTenant> CreateTenantAsync(
        PostgresFixture fixture,
        bool companyActive = true,
        bool userActive = true,
        string permissionsJson = "{}")
    {
        await fixture.VerifyTargetAsync();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var company = new Company
        {
            Id = Guid.NewGuid(),
            Name = $"Test Co {suffix}",
            Domain = $"t-{suffix}.test",
            LogoUrl = string.Empty,
            Timezone = "Asia/Kolkata",
            IsActive = companyActive
        };

        var role = new Role
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            Name = "Admin",
            DisplayName = "Administrator",
            Permissions = permissionsJson,
            IsSystem = true
        };

        var password = NewPassword();
        var user = new User
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            RoleId = role.Id,
            Name = "Test Admin",
            Email = $"admin-{suffix}@{company.Domain}",
            PasswordHash = HashPassword(password),
            IsActive = userActive
        };

        await using var db = fixture.CreateDbContext();
        db.Companies.Add(company);
        db.Roles.Add(role);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return new TestTenant(company.Id, company.Domain, role.Id, user.Id, user.Email, password);
    }

    /// <summary>A company and an "Admin" role without any user (no password hashing), for tests that insert user rows themselves.</summary>
    public static async Task<TestTenant> CreateCompanyAndRoleAsync(PostgresFixture fixture)
    {
        await fixture.VerifyTargetAsync();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var company = new Company { Id = Guid.NewGuid(), Name = $"Bare Co {suffix}", Domain = $"b-{suffix}.test", LogoUrl = string.Empty, Timezone = "Asia/Kolkata", IsActive = true };
        var role = new Role { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Admin", DisplayName = "Administrator", Permissions = "{}", IsSystem = true };

        await using var db = fixture.CreateDbContext();
        db.Companies.Add(company);
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        return new TestTenant(company.Id, company.Domain, role.Id, Guid.Empty, string.Empty, string.Empty);
    }

    public static async Task AddSessionAsync(PostgresFixture fixture, Guid userId, string token, DateTime expiresAtUtc)
    {
        await fixture.VerifyTargetAsync();

        await using var db = fixture.CreateDbContext();
        db.Sessions.Add(new Session
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = HashToken(token),
            ExpiresAt = expiresAtUtc
        });
        await db.SaveChangesAsync();
    }

    public static async Task<List<Session>> GetSessionsAsync(PostgresFixture fixture, Guid userId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Sessions.AsNoTracking().Where(s => s.UserId == userId).ToListAsync();
    }
}

public static class TestJwt
{
    /// <summary>Mints a JWT with the same claim set as the production JwtTokenService, signed with the given secret.</summary>
    public static string Create(
        string secret,
        string issuer,
        string audience,
        Guid userId,
        Guid companyId,
        string email,
        string role = "Admin",
        DateTime? notBeforeUtc = null,
        DateTime? expiresUtc = null)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("companyId", companyId.ToString()),
            new Claim(ClaimTypes.Role, role)
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: notBeforeUtc,
            expires: expiresUtc ?? DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
