using FluentValidation;

namespace HRMS.Application.Features.Auth.Commands.Login
{
    public class LoginCommandValidator : AbstractValidator<LoginCommand>
    {
        public LoginCommandValidator()
        {
            RuleFor(x => x.Email)
                .NotEmpty()
                .MaximumLength(255)
                .EmailAddress();

            RuleFor(x => x.Password)
                .NotEmpty()
                .MaximumLength(128);
        }
    }
}
