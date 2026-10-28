using FluentValidation;
using FluentValidation.Results;
using HRMS.Application.Interfaces;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Infrastructure.Authorization
{
    public sealed class ReferenceValidator : IReferenceValidator
    {
        private readonly HrmsDbContext _dbContext;
        private readonly ICurrentUser _currentUser;

        public ReferenceValidator(HrmsDbContext dbContext, ICurrentUser currentUser)
        {
            _dbContext = dbContext;
            _currentUser = currentUser;
        }

        public async Task EnsureEmployeeInCompanyAsync(Guid employeeId, string propertyName, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            Ensure(await _dbContext.Employees.AsNoTracking().AnyAsync(e => e.Id == employeeId && e.CompanyId == companyId, cancellationToken), propertyName);
        }

        public async Task EnsureDepartmentInCompanyAsync(Guid departmentId, string propertyName, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            Ensure(await _dbContext.Departments.AsNoTracking().AnyAsync(d => d.Id == departmentId && d.CompanyId == companyId, cancellationToken), propertyName);
        }

        public async Task EnsureUserInCompanyAsync(Guid userId, string propertyName, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            Ensure(await _dbContext.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.CompanyId == companyId, cancellationToken), propertyName);
        }

        public async Task EnsureRoleAssignableAsync(Guid roleId, string propertyName, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            Ensure(await _dbContext.Roles.AsNoTracking().AnyAsync(
                r => r.Id == roleId && (r.CompanyId == companyId || (r.CompanyId == null && r.IsSystem)), cancellationToken), propertyName);
        }

        private static void Ensure(bool found, string propertyName)
        {
            if (!found)
            {
                // Same message whether the row is missing or belongs to another company.
                throw new ValidationException(new[] { new ValidationFailure(propertyName, $"{propertyName} does not exist.") });
            }
        }
    }
}
