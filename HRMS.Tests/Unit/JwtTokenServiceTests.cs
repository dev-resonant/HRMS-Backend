using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using HRMS.Infrastructure.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace HRMS.Tests.Unit;

public class JwtTokenServiceTests
{
    private const string Secret = "unit-test-secret-unit-test-secret-unit-test-secret-0123456789";

    private static JwtTokenService Create(Action<Dictionary<string, string?>>? mutate = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = Secret,
            ["Jwt:Issuer"] = "test-issuer",
            ["Jwt:Audience"] = "test-audience",
            ["Jwt:ExpirationMinutes"] = "480"
        };
        mutate?.Invoke(settings);

        return new JwtTokenService(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    [Fact]
    public void Token_carries_the_expected_claims_issuer_audience_and_expiry()
    {
        var userId = Guid.NewGuid();
        var companyId = Guid.NewGuid();

        var (token, expiresAt) = Create().GenerateToken(userId, companyId, "user@example.test", "Admin");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("test-issuer", jwt.Issuer);
        Assert.Contains("test-audience", jwt.Audiences);
        Assert.Equal(userId.ToString(), jwt.Claims.Single(c => c.Type == ClaimTypes.NameIdentifier).Value);
        Assert.Equal(companyId.ToString(), jwt.Claims.Single(c => c.Type == "companyId").Value);
        Assert.Equal("user@example.test", jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Email).Value);
        Assert.Equal("Admin", jwt.Claims.Single(c => c.Type == ClaimTypes.Role).Value);
        Assert.InRange((expiresAt - DateTime.UtcNow).TotalMinutes, 479, 481);
        Assert.InRange((jwt.ValidTo - expiresAt).TotalSeconds, -2, 2);
    }

    [Fact]
    public void Token_signature_validates_with_the_configured_secret_and_fails_with_another()
    {
        var (token, _) = Create().GenerateToken(Guid.NewGuid(), Guid.NewGuid(), "user@example.test", "Admin");
        var handler = new JwtSecurityTokenHandler();

        TokenValidationParameters Parameters(string secret) => new()
        {
            ValidateIssuer = true, ValidIssuer = "test-issuer",
            ValidateAudience = true, ValidAudience = "test-audience",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret))
        };

        handler.ValidateToken(token, Parameters(Secret), out _);
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(token, Parameters(new string('x', 64)), out _));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData(null)]
    public void Invalid_expiration_configuration_is_rejected(string? minutes)
    {
        var service = Create(s => s["Jwt:ExpirationMinutes"] = minutes);

        Assert.Throws<InvalidOperationException>(() => service.GenerateToken(Guid.NewGuid(), Guid.NewGuid(), "a@b.test", "Admin"));
    }

    [Theory]
    [InlineData("Jwt:Secret")]
    [InlineData("Jwt:Issuer")]
    [InlineData("Jwt:Audience")]
    public void Missing_jwt_settings_are_rejected(string key)
    {
        var service = Create(s => s.Remove(key));

        Assert.Throws<InvalidOperationException>(() => service.GenerateToken(Guid.NewGuid(), Guid.NewGuid(), "a@b.test", "Admin"));
    }
}
