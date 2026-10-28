using HRMS.Infrastructure.Common;

namespace HRMS.Tests.Unit;

public class TokenHasherTests
{
    private readonly TokenHasher _hasher = new();

    [Fact]
    public void Hash_matches_the_known_SHA256_vector_in_uppercase_hex()
    {
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", _hasher.Hash("abc"));
    }

    [Fact]
    public void Hash_is_deterministic_and_input_sensitive()
    {
        Assert.Equal(_hasher.Hash("token-a"), _hasher.Hash("token-a"));
        Assert.NotEqual(_hasher.Hash("token-a"), _hasher.Hash("token-b"));
        Assert.Equal(64, _hasher.Hash("anything").Length);
    }
}
