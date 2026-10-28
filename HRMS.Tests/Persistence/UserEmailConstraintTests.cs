using HRMS.Domain.Common;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HRMS.Tests.Persistence;

/// <summary>
/// Database-level proof of the email policy in the fully migrated disposable database: the CK_Users_Email_Normalized check
/// plus the company-scoped unique index. Raw SQL is used where the User.Email setter would otherwise normalize the value.
/// </summary>
[Collection(PostgresCollection.Name)]
public class UserEmailConstraintTests
{
    private readonly PostgresFixture _fx;

    public UserEmailConstraintTests(PostgresFixture fx) => _fx = fx;

    private async Task<PostgresException> RawInsertFailureAsync(TestTenant tenant, string email)
    {
        await _fx.VerifyTargetAsync();
        await using var connection = new NpgsqlConnection(_fx.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "insert into \"Users\"(\"Id\",\"CompanyId\",\"RoleId\",\"Name\",\"Email\",\"PasswordHash\",\"IsActive\",\"CreatedAt\") " +
            "values (@id,@company,@role,'Raw',@email,'x',true,now())", connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company", tenant.CompanyId);
        command.Parameters.AddWithValue("role", tenant.RoleId);
        command.Parameters.AddWithValue("email", email);

        return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    private async Task RawInsertAsync(TestTenant tenant, string email)
    {
        await _fx.VerifyTargetAsync();
        await using var connection = new NpgsqlConnection(_fx.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "insert into \"Users\"(\"Id\",\"CompanyId\",\"RoleId\",\"Name\",\"Email\",\"PasswordHash\",\"IsActive\",\"CreatedAt\") " +
            "values (@id,@company,@role,'Raw',@email,'x',true,now())", connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("company", tenant.CompanyId);
        command.Parameters.AddWithValue("role", tenant.RoleId);
        command.Parameters.AddWithValue("email", email);

        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("Admin@Mixed.TEST")]
    [InlineData("UPPER@EXAMPLE.TEST")]
    public async Task The_database_rejects_an_email_that_is_not_lowercase(string email)
    {
        var tenant = await TestData.CreateCompanyAndRoleAsync(_fx);

        var failure = await RawInsertFailureAsync(tenant, email);

        Assert.Equal("23514", failure.SqlState);                                    // check_violation
        Assert.Equal("CK_Users_Email_Normalized", failure.ConstraintName);
    }

    public static IEnumerable<object[]> EdgeWhitespace()
    {
        foreach (var codePoint in UserEmailPolicy.TrimmedWhitespaceCodePoints)
        {
            yield return new object[] { codePoint, true };          // leading
            yield return new object[] { codePoint, false };         // trailing
        }
    }

    [Theory]
    [MemberData(nameof(EdgeWhitespace))]
    public async Task The_database_rejects_every_whitespace_character_the_application_trims_at_either_end(int codePoint, bool leading)
    {
        // Covers: space, tab, LF, VT, FF, CR, NEL, no-break space, Ogham space, U+2000-U+200A, line/paragraph separators,
        // narrow no-break space, medium mathematical space and the ideographic space - 25 characters, leading and trailing.
        var character = char.ConvertFromUtf32(codePoint);
        var email = leading ? character + "padded@example.test" : "padded@example.test" + character;
        Assert.NotEqual(email, EmailNormalizer.Normalize(email));                   // the application WOULD have trimmed this
        var tenant = await TestData.CreateCompanyAndRoleAsync(_fx);

        var failure = await RawInsertFailureAsync(tenant, email);

        Assert.Equal("23514", failure.SqlState);
        Assert.Equal("CK_Users_Email_Normalized", failure.ConstraintName);
    }

    [Theory]
    [InlineData("plain@example.test")]                              // already normalized
    [InlineData("first.last+tag@sub.example.test")]
    [InlineData("inner space@example.test")]                        // inner whitespace: neither side changes it
    [InlineData("inner\ttab@example.test")]
    [InlineData("​zero-width-leading@example.test")]           // U+200B is NOT whitespace for string.Trim(), so it is not trimmed either
    [InlineData("zero-width-trailing@example.test​")]
    [InlineData("﻿bom-leading@example.test")]                  // U+FEFF likewise
    public async Task The_database_accepts_exactly_the_addresses_the_application_leaves_unchanged(string email)
    {
        Assert.Equal(email, EmailNormalizer.Normalize(email));                      // application: no change
        var tenant = await TestData.CreateCompanyAndRoleAsync(_fx);

        await RawInsertAsync(tenant, email);                                        // database: no exception

        await using var db = _fx.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.CompanyId == tenant.CompanyId));
    }

    [Fact]
    public async Task Two_users_in_one_company_cannot_have_emails_that_differ_only_by_case_through_EF()
    {
        var tenant = await TestData.CreateTenantAsync(_fx);

        await using var db = _fx.CreateDbContext();
        db.Users.Add(new User { Id = Guid.NewGuid(), CompanyId = tenant.CompanyId, RoleId = tenant.RoleId, Name = "First", Email = "Dup@Same.TEST", PasswordHash = "x" });
        await db.SaveChangesAsync();

        db.Users.Add(new User { Id = Guid.NewGuid(), CompanyId = tenant.CompanyId, RoleId = tenant.RoleId, Name = "Second", Email = "dup@same.test", PasswordHash = "x" });

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var inner = Assert.IsType<PostgresException>(failure.InnerException);
        Assert.Equal("23505", inner.SqlState);                                      // unique_violation on (CompanyId, Email)
    }

    [Fact]
    public async Task Two_users_in_one_company_cannot_differ_only_by_case_even_through_raw_SQL()
    {
        var tenant = await TestData.CreateTenantAsync(_fx);
        var existing = $"raw-dup@{tenant.Domain}";

        await using (var db = _fx.CreateDbContext())
        {
            db.Users.Add(new User { Id = Guid.NewGuid(), CompanyId = tenant.CompanyId, RoleId = tenant.RoleId, Name = "First", Email = existing, PasswordHash = "x" });
            await db.SaveChangesAsync();
        }

        var failure = await RawInsertFailureAsync(tenant, existing.ToUpperInvariant());

        Assert.Equal("23514", failure.SqlState);                                    // rejected by the check before uniqueness matters
    }

    [Fact]
    public async Task Different_companies_may_use_the_same_email_address()
    {
        var first = await TestData.CreateTenantAsync(_fx);
        var second = await TestData.CreateTenantAsync(_fx);

        await using var db = _fx.CreateDbContext();
        db.Users.Add(new User { Id = Guid.NewGuid(), CompanyId = first.CompanyId, RoleId = first.RoleId, Name = "A", Email = "shared@example.test", PasswordHash = "x" });
        db.Users.Add(new User { Id = Guid.NewGuid(), CompanyId = second.CompanyId, RoleId = second.RoleId, Name = "B", Email = "SHARED@Example.Test", PasswordHash = "x" });

        await db.SaveChangesAsync();                                                // company-scoped uniqueness: no exception

        Assert.Equal(2, await db.Users.AsNoTracking().CountAsync(u => u.Email == "shared@example.test"));
    }

    [Fact]
    public async Task A_user_saved_through_EF_is_stored_trimmed_and_lowercase()
    {
        var tenant = await TestData.CreateTenantAsync(_fx);
        var id = Guid.NewGuid();

        await using (var db = _fx.CreateDbContext())
        {
            db.Users.Add(new User { Id = id, CompanyId = tenant.CompanyId, RoleId = tenant.RoleId, Name = "N", Email = "  Stored@CASE.Test ", PasswordHash = "x" });
            await db.SaveChangesAsync();
        }

        await using var verify = _fx.CreateDbContext();
        Assert.Equal("stored@case.test", (await verify.Users.AsNoTracking().SingleAsync(u => u.Id == id)).Email);
    }
}
