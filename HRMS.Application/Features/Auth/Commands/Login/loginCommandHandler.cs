using HRMS.Application.Common.Exceptions;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Domain.Common;
using HRMS.Domain.Entities;
using MediatR;
using Microsoft.Extensions.Logging;
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
        private readonly IClientInfo _clientInfo;
        private readonly IAuditWriter _auditWriter;
        private readonly ILogger<LoginCommandHandler> _logger;


        public LoginCommandHandler(ITenantContext tenantContext, IUserRepository userRepository, IPasswordHasher passwordHasher, IJwtTokenService jwtTokenService, ISessionRepository sessionRepository,ITokenHasher tokenHasher, IClientInfo clientInfo, IAuditWriter auditWriter, ILogger<LoginCommandHandler> logger)
        {
            _auditWriter = auditWriter;
            _logger = logger;
            _clientInfo = clientInfo;
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

            var email = EmailNormalizer.Normalize(request.Email);

            var user = await _userRepository.GetByEmailAsync(email, _tenantContext.CompanyId, cancellationToken);

            if (user is null)
            {
                throw new UnauthorizedAccessException("Invalid email or password.");
            }

            var passwordValid = _passwordHasher.Verify(request.Password, user.PasswordHash);

            if (!passwordValid)
            {
                // Specification section 4, step 3: log the failed attempt (with IP). The account exists here, so the audit
                // row can name it. A failure to write the audit entry must not change the outcome for the caller (still 401),
                // but it must not be silent either.
                try
                {
                    await _auditWriter.RecordFailedLoginAsync(user.Id, user.CompanyId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "The failed-login audit entry for user {UserId} could not be written.", user.Id);
                }

                throw new UnauthorizedAccessException("Invalid email or password.");
            }

            // Checked only after the password is verified so account status is not disclosed without credentials.
            if (!user.IsActive)
            {
                throw new ForbiddenException("Account is deactivated.");
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
                ExpiresAt = jwt.ExpiresAt,
                IpAddress = _clientInfo.IpAddress,
                UserAgent = _clientInfo.UserAgent
            };

            await _sessionRepository.AddAsync(session,cancellationToken);

            await _userRepository.UpdateLastLoginAsync(user.Id, user.CompanyId, DateTime.UtcNow, cancellationToken);

            return new LoginResponseDto
            {
                Token = jwt.Token,
                ExpiresAt = jwt.ExpiresAt,
                User = new UserInfoDto
                {
                    Id = user.Id,
                    Name = user.Name,
                    Email = user.Email,
                    Role = user.Role.Name,
                    RoleDisplayName = user.Role.DisplayName,
                    Permissions = RolePermissions.Parse(user.Role.Permissions),
                    CompanyId = user.CompanyId,
                    PasswordChangeRequired = user.IsFirstLogin,
                }
            };
        }
    }
}
