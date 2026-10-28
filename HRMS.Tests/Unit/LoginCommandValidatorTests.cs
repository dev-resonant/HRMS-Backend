using HRMS.Application.Features.Auth.Commands.Login;

namespace HRMS.Tests.Unit;

public class LoginCommandValidatorTests
{
    private readonly LoginCommandValidator _validator = new();

    private static LoginCommand Command(string email, string password) => new() { Email = email, Password = password };

    [Fact]
    public void A_well_formed_command_is_valid()
    {
        Assert.True(_validator.Validate(Command("user@example.test", "a-password")).IsValid);
    }

    [Theory]
    [InlineData("", "pw", "Email")]
    [InlineData("not-an-email", "pw", "Email")]
    [InlineData("user@example.test", "", "Password")]
    public void Invalid_commands_report_the_offending_property(string email, string password, string property)
    {
        var result = _validator.Validate(Command(email, password));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == property);
    }

    [Fact]
    public void Email_length_limit_is_255_characters()
    {
        var atLimit = new string('a', 250) + "@x.io";        // exactly 255 characters
        var overLimit = new string('a', 251) + "@x.io";      // 256 characters

        Assert.Equal(255, atLimit.Length);
        Assert.True(_validator.Validate(Command(atLimit, "pw")).IsValid);

        var result = _validator.Validate(Command(overLimit, "pw"));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Email");
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void Password_length_limit_is_128_characters(int length, bool expectedValid)
    {
        Assert.Equal(expectedValid, _validator.Validate(Command("user@example.test", new string('p', length))).IsValid);
    }
}
