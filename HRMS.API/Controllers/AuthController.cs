using HRMS.Application.Features.Auth.Commands.Login;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using HRMS.Application.Features.Auth;
using HRMS.Application.Features.Auth.Commands.ChangePassword;
using HRMS.Application.Features.Auth.Commands.Logout;
using HRMS.Application.Features.Auth.Commands.SetPassword;
using HRMS.Application.Features.Auth.Queries.Me;
using HRMS.API.Authorization;
using HRMS.API.Extensions;
using HRMS.API.RateLimiting;
using HRMS.Application.Interfaces;
using HRMS.Domain.Common;
using System.Security.Claims;

namespace HRMS.API.Controllers
{

    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {

        private readonly IMediator _mediator;
        private readonly AttemptLimiter _attempts;
        private readonly ITenantContext _tenant;

        public AuthController(IMediator mediator, AttemptLimiter attempts, ITenantContext tenant)
        {
            _mediator = mediator;
            _attempts = attempts;
            _tenant = tenant;
        }

        [HttpPost("login")]
        [AllowAnonymous]
        [AuthRateLimit(AuthRateLimitPolicy.Login)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<ActionResult<LoginResponseDto>> Login([FromBody] LoginCommand command, CancellationToken cancellationToken)
        {
            // Budget per client address AND tenant AND e-mail; identical for accounts that exist and ones that do not.
            _attempts.EnforceLogin(
                _tenant.IsResolved ? _tenant.CompanyId : Guid.Empty,
                RateLimitingSetup.AddressKey(HttpContext.Connection.RemoteIpAddress),
                EmailNormalizer.Normalize(command.Email ?? string.Empty));

            var result = await _mediator.Send(command, cancellationToken);

            return Ok(result);
        }

        [HttpPost("logout")]
        [SelfService(requireActiveAccount: false)]
        public async Task<IActionResult> Logout(CancellationToken cancellationToken)
        {
            var token = Request.GetBearerToken();

            if (token is null)
            {
                return Unauthorized(new { message = "Invalid token." });
            }

            await _mediator.Send(new LogoutCommand(token), cancellationToken);

            return Ok(new { message = "Logged out successfully." });
        }

        /// <summary>Authenticated: change the password (required first when the account still has to set one). Keeps this login, ends all others.</summary>
        [HttpPost("change-password")]
        [SelfService(AllowWhilePasswordChangePending = true)]
        [AuthRateLimit(AuthRateLimitPolicy.ChangePassword)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
        {
            var token = Request.GetBearerToken();

            if (token is null)
            {
                return Unauthorized(new { message = "Invalid token." });
            }

            await _mediator.Send(new ChangePasswordCommand(token, request.CurrentPassword, request.NewPassword), cancellationToken);

            return Ok(new { message = "Password changed." });
        }

        /// <summary>Anonymous: complete onboarding with the one-time token from the invitation. Does not log the user in.</summary>
        [HttpPost("set-password")]
        [AllowAnonymous]
        [AuthRateLimit(AuthRateLimitPolicy.SetPassword)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<IActionResult> SetPassword([FromBody] SetPasswordRequest request, CancellationToken cancellationToken)
        {
            // Budget per setup token (whoever submits it): hammering one link cannot be used to probe or flood the account.
            _attempts.EnforceSetPassword(request.Token.Trim());

            await _mediator.Send(new SetPasswordCommand(request.Token, request.NewPassword), cancellationToken);

            return Ok(new { message = "Password set. You can now log in." });
        }

        [HttpGet("me")]
        [SelfService(AllowWhilePasswordChangePending = true)]

        public async Task<ActionResult<MeResponseDto>> Me(CancellationToken cancellationToken)
        {
            var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (!Guid.TryParse(userIdValue, out var userId))
            {
                return Unauthorized(new
                {
                    message = "Invalid user identity."
                });
            }

            var result = await _mediator.Send(new MeQuery(userId), cancellationToken);

            return Ok(result);
        }
    }
}
