using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using MediatR;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Application.Features.Auth.Commands.Login
{
    public class LoginCommandHandler : IRequestHandler<LoginCommand, LoginResponseDto>
    {

        private readonly ITenantContext _tenantContext;
        private readonly IUserRepository _userRepository;
        private readonly IPasswordHasher _passwordHasher;
        private readonly IJwtTokenService _jwtTokenService;
        private readonly ISessionRepository _sessionRepository;
        private readonly ITokenHasher _tokenHasher;


        public LoginCommandHandler(ITenantContext tenantContext, IUserRepository userRepository, IPasswordHasher passwordHasher, IJwtTokenService jwtTokenService, ISessionRepository sessionRepository,ITokenHasher tokenHasher)
        {
            _tenantContext = tenantContext;
            _userRepository = userRepository;
            _passwordHasher = passwordHasher;
            _jwtTokenService = jwtTokenService;
            _sessionRepository = sessionRepository;
            _tokenHasher = tokenHasher;
        }

        public async Task<LoginResponseDto> Handle(LoginCommand request, CancellationToken cancellationToken)
        {
            if(!_tenantContext.IsResolved)
            {
                throw new UnauthorizedAccessException("Tenant could not be resolved.");
            }

            var email = request.Email.Trim().ToLowerInvariant();

            var user = await _userRepository.GetByEmailAsync(email, _tenantContext.CompanyId, cancellationToken);

            if (user is null || !user.IsActive)
            {
                throw new UnauthorizedAccessException("Invalid email or password.");
            }

            var passwordValid = _passwordHasher.Verify(request.Password, user.PasswordHash);

            if (!passwordValid)
            {
                throw new UnauthorizedAccessException("Invalid email or password.");
            }

            var rolename = user.Role.Name;

            var jwt = _jwtTokenService.GenerateToken(
                user.Id,
                user.CompanyId,
                user.Email,
                rolename);

            var tokenHash = _tokenHasher.Hash(jwt.Token);

            var session = new Session
            {
                UserId = user.Id,
                TokenHash = tokenHash,
                ExpiresAt = jwt.ExpiresAt
            };

            await _sessionRepository.AddAsync(session,cancellationToken);

            user.LastLogin = DateTime.UtcNow;

            await _userRepository.UpdateAsync(user,cancellationToken);

            return new LoginResponseDto
            {
                Token = jwt.Token,
                ExpiresAt = jwt.ExpiresAt,
                User = new UserInfoDto
                {
                    Id = user.Id,
                    Name = user.Name,
                    Email = user.Email,
                    Role = user.Role.DisplayName,
                    CompanyId = user.CompanyId,
                }
            };
        }
    }
}
