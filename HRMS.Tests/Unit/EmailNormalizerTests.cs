using System.Globalization;
using HRMS.Domain.Common;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;

namespace HRMS.Tests.Unit;

public class EmailNormalizerTests
{
    [Theory]
    [InlineData("user@example.test", "user@example.test")]
    [InlineData("User@Example.TEST", "user@example.test")]
    [InlineData("USER@EXAMPLE.TEST", "user@example.test")]
    [InlineData("  user@example.test  ", "user@example.test")]
    [InlineData("\t Mixed.Case@Example.COM \r\n", "mixed.case@example.com")]
    public void Normalize_trims_surrounding_whitespace_and_lowercases(string input, string expected)
    {
        Assert.Equal(expected, EmailNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var once = EmailNormalizer.Normalize("  Mixed@Case.TEST ");

        Assert.Equal(once, EmailNormalizer.Normalize(once));
    }

    [Fact]
    public void Normalize_uses_the_invariant_culture_not_the_current_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");          // Turkish culture lowercases 'I' to dotless 'ı'

            Assert.Equal("title@example.test", EmailNormalizer.Normalize("TITLE@Example.test"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    public static IEnumerable<object[]> PolicyCodePoints() => UserEmailPolicy.TrimmedWhitespaceCodePoints.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(PolicyCodePoints))]
    public void Normalize_removes_each_policy_whitespace_character_at_both_ends(int codePoint)
    {
        var character = char.ConvertFromUtf32(codePoint);

        Assert.Equal("a@b.test", EmailNormalizer.Normalize(character + "A@B.test" + character));
        Assert.Equal("a@b.test", EmailNormalizer.Normalize(character + character + "a@b.test"));
    }

    [Fact]
    public void The_database_policy_list_is_exactly_the_set_of_BMP_characters_that_string_Trim_removes()
    {
        var trimmedByDotNet = new List<int>();
        for (var codePoint = 0; codePoint <= 0xFFFF; codePoint++)
        {
            if (codePoint is >= 0xD800 and <= 0xDFFF) continue;                 // surrogate halves are not characters on their own

            var character = ((char)codePoint).ToString();
            var removedAtStart = (character + "a").Trim() == "a";
            var removedAtEnd = ("a" + character).Trim() == "a";

            Assert.Equal(removedAtStart, removedAtEnd);                         // Trim() is symmetric
            Assert.Equal(char.IsWhiteSpace((char)codePoint), removedAtStart);   // and equals char.IsWhiteSpace

            if (removedAtStart) trimmedByDotNet.Add(codePoint);
        }

        Assert.Equal(25, trimmedByDotNet.Count);
        Assert.Equal(trimmedByDotNet, UserEmailPolicy.TrimmedWhitespaceCodePoints.OrderBy(c => c).ToList());
    }

    [Theory]
    [InlineData(0x200B)]    // zero-width space
    [InlineData(0x200C)]    // zero-width non-joiner
    [InlineData(0x200D)]    // zero-width joiner
    [InlineData(0x2060)]    // word joiner
    [InlineData(0xFEFF)]    // zero-width no-break space / BOM
    [InlineData(0x00AD)]    // soft hyphen
    public void Invisible_characters_that_Trim_does_not_remove_are_left_alone_by_both_sides(int codePoint)
    {
        var email = char.ConvertFromUtf32(codePoint) + "a@b.test" + char.ConvertFromUtf32(codePoint);

        Assert.Equal(email, EmailNormalizer.Normalize(email));
        Assert.DoesNotContain(codePoint, UserEmailPolicy.TrimmedWhitespaceCodePoints);
    }

    [Fact]
    public void Normalize_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => EmailNormalizer.Normalize(null!));
    }

    [Fact]
    public void The_User_entity_stores_its_email_normalized_whatever_the_caller_passes()
    {
        var user = new User { Email = "  Admin@Mixed.TEST " };

        Assert.Equal("admin@mixed.test", user.Email);

        user.Email = "Other@CASE.test";
        Assert.Equal("other@case.test", user.Email);
    }
}
