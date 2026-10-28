using MediatR;

namespace HRMS.Application.Features.Auth.Commands.Logout
{
    public class LogoutCommand : IRequest
    {
        // Raw bearer token; used only to compute the session hash, never stored or logged.
        public string Token { get; }

        public LogoutCommand(string token)
        {
            Token = token;
        }
    }
}
