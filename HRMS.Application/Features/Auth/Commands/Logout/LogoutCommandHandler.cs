using HRMS.Application.Interfaces;
using MediatR;

namespace HRMS.Application.Features.Auth.Commands.Logout
{
    public class LogoutCommandHandler : IRequestHandler<LogoutCommand>
    {
        private readonly ISessionRepository _sessionRepository;
        private readonly ITokenHasher _tokenHasher;

        public LogoutCommandHandler(ISessionRepository sessionRepository, ITokenHasher tokenHasher)
        {
            _sessionRepository = sessionRepository;
            _tokenHasher = tokenHasher;
        }

        public async Task Handle(LogoutCommand request, CancellationToken cancellationToken)
        {
            var tokenHash = _tokenHasher.Hash(request.Token);

            await _sessionRepository.DeleteByTokenHashAsync(tokenHash, cancellationToken);
        }
    }
}
