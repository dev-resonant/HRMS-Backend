using System.Globalization;
using HRMS.Domain.Common;
using HRMS.Domain.Entities;

namespace HRMS.Tests.Unit;

public class DomainNormalizerTests
{
    [Theory]
    [InlineData("example.test", "example.test")]
    [InlineData("Example.TEST", "example.test")]
    [InlineData("  example.test  ", "example.test")]
    [InlineData("\tExample.Test\r\n", "example.test")]
    [InlineData(" EXAMPLE.test　", "example.test")]
    public void Normalize_trims_surrounding_whitespace_and_lowercases(string input, string expected)
    {
        Assert.Equal(expected, DomainNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_is_idempotent_and_uses_the_invariant_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");           // would lowercase 'I' to dotless 'ı' if the culture were used

            var once = DomainNormalizer.Normalize("  INFO.Example.TEST ");

            Assert.Equal("info.example.test", once);
            Assert.Equal(once, DomainNormalizer.Normalize(once));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Normalize_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => DomainNormalizer.Normalize(null!));
    }

    [Fact]
    public void The_Company_entity_stores_its_domain_normalized_whatever_the_caller_passes()
    {
        var company = new Company { Domain = "  Acme.EXAMPLE.test \n" };

        Assert.Equal("acme.example.test", company.Domain);

        company.Domain = "OTHER.Test";
        Assert.Equal("other.test", company.Domain);
    }

    [Fact]
    public void A_new_Company_has_an_empty_domain_until_one_is_assigned()
    {
        Assert.Equal(string.Empty, new Company().Domain);
    }
}
