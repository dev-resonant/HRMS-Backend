using System;
using System.Collections.Generic;
using System.Text;
using HRMS.Application.Common.Exceptions;
using HRMS.Application.Interfaces;
using MediatR;
using System.Security.Claims;

namespace HRMS.Application.Features.Auth.Queries.Me
{
    public class MeQueryHandler : IRequestHandler<MeQuery, MeResponseDto>
    {
        private readonly IUserRepository _userRepository;
        private readonly ITenantContext _tenantContext;

        private readonly ICurrentUser _currentUserContext;

        public MeQueryHandler(
            IUserRepository userRepository,
            ITenantContext tenantContext,
            ICurrentUser currentUserContext)
        {
            _userRepository = userRepository;
            _tenantContext = tenantContext;
            _currentUserContext = currentUserContext;
        }

        public async Task<MeResponseDto> Handle(
            MeQuery request,
            CancellationToken cancellationToken)
        {
            if (!_tenantContext.IsResolved)
            {
                throw new UnauthorizedAccessException(
                    "Tenant could not be resolved.");
            }

            var currentUser = await _userRepository.GetByIdAsync(
                request.UserId,
                _tenantContext.CompanyId,
                cancellationToken);

            if (currentUser is null)
            {
                throw new UnauthorizedAccessException(
                    "User not found.");
            }

            if (!currentUser.IsActive)
            {
                throw new ForbiddenException(
                    "Account is deactivated.");
            }

            return new MeResponseDto
            {
                Id = currentUser.Id,
                CompanyId = currentUser.CompanyId,
                Name = currentUser.Name,
                Email = currentUser.Email,
                Role = _currentUserContext.RoleKey,
                RoleDisplayName = _currentUserContext.RoleDisplayName,
                EmployeeId = _currentUserContext.EmployeeId,
                Permissions = _currentUserContext.Permissions,
                IsActive = currentUser.IsActive,
                PasswordChangeRequired = _currentUserContext.PasswordChangeRequired
            };
        }
    }
}
