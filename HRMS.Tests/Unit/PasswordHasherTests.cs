using HRMS.Infrastructure.Common;
using Xunit.Abstractions;

namespace HRMS.Tests.Unit;

public class PasswordHasherTests
{
    private readonly BCryptPasswordHasher _hasher = new();
    private readonly ITestOutputHelper _output;

    public PasswordHasherTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Hash_then_Verify_round_trips_and_rejects_a_wrong_password()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.True(_hasher.Verify("correct horse battery staple", hash));
        Assert.False(_hasher.Verify("wrong password", hash));
    }

    [Fact]
    public void Hashes_are_salted_bcrypt_hashes_and_never_contain_the_password()
    {
        var first = _hasher.Hash("same-password");
        var second = _hasher.Hash("same-password");

        Assert.NotEqual(first, second);
        Assert.StartsWith("$2", first);
        Assert.DoesNotContain("same-password", first);
        _output.WriteLine($"Current production bcrypt cost factor: {CostOf(first)}");
    }

    [Fact]
    public void Production_hashing_cost_is_at_least_12()
    {
        // Specification Table 3: "bcrypt hash (cost factor 12+)".
        Assert.True(CostOf(_hasher.Hash("any-password")) >= 12);
    }

    [Fact]
    public void New_hashes_use_exactly_the_configured_work_factor_of_12()
    {
        Assert.Equal(12, BCryptPasswordHasher.WorkFactor);
        Assert.Equal(12, CostOf(_hasher.Hash("any-password")));
        Assert.StartsWith("$2a$12$", _hasher.Hash("any-password"));
    }

    [Fact]
    public void An_existing_cost_11_hash_still_verifies_and_rejects_a_wrong_password()
    {
        // Hashes created before the work-factor change used the library default of 11 (e.g. the production admin).
        var legacyHash = BCrypt.Net.BCrypt.HashPassword("legacy-password", 11);

        Assert.Equal(11, CostOf(legacyHash));
        Assert.True(_hasher.Verify("legacy-password", legacyHash));
        Assert.False(_hasher.Verify("not-the-password", legacyHash));
    }

    private static int CostOf(string hash) => int.Parse(hash.Split('$')[2]);
}
