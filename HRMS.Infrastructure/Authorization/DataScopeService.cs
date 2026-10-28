using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Infrastructure.Authorization
{
    public sealed class DataScopeService : IDataScopeService
    {
        private readonly HrmsDbContext _dbContext;
        private readonly ICurrentUser _currentUser;

        public DataScopeService(HrmsDbContext dbContext, ICurrentUser currentUser)
        {
            _dbContext = dbContext;
            _currentUser = currentUser;
        }

        public DataScope GetEmployeeReadScope()
        {
            if (_currentUser.HasPermission(PermissionNames.Employees.Module, PermissionNames.Employees.ViewAll))
            {
                return DataScope.Company;
            }

            if (_currentUser.HasPermission(PermissionNames.Employees.Module, PermissionNames.Employees.ViewTeam))
            {
                return DataScope.Team;
            }

            if (_currentUser.HasPermission(PermissionNames.Employees.Module, PermissionNames.Employees.ViewOwn))
            {
                return DataScope.Own;
            }

            return DataScope.None;
        }

        public DataScope GetEmployeeWriteScope()
        {
            if (_currentUser.HasPermission(PermissionNames.Employees.Module, PermissionNames.Employees.EditAny))
            {
                return DataScope.Company;
            }

            if (_currentUser.HasPermission(PermissionNames.Employees.Module, PermissionNames.Employees.EditOwn))
            {
                return DataScope.Own;
            }

            return DataScope.None;
        }

        public IQueryable<Employee> ApplyEmployeeReadScope(IQueryable<Employee> employees)
            => Apply(employees, GetEmployeeReadScope());

        public Task<bool> CanReadEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default)
            => Apply(_dbContext.Employees.AsNoTracking(), GetEmployeeReadScope()).AnyAsync(e => e.Id == employeeId, cancellationToken);

        public Task<bool> CanWriteEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default)
            => Apply(_dbContext.Employees.AsNoTracking(), GetEmployeeWriteScope()).AnyAsync(e => e.Id == employeeId, cancellationToken);

        public DataScope GetDepartmentReadScope()
        {
            if (_currentUser.HasPermission(PermissionNames.Departments.Module, PermissionNames.Departments.ViewAll))
            {
                return DataScope.Company;
            }

            if (_currentUser.HasPermission(PermissionNames.Departments.Module, PermissionNames.Departments.ViewOwnDepartment))
            {
                return DataScope.Own;
            }

            return DataScope.None;
        }

        public IQueryable<Department> ApplyDepartmentReadScope(IQueryable<Department> departments)
        {
            // Reading CompanyId throws when there is no resolved current user, so a missing context never widens access.
            var companyId = _currentUser.CompanyId;
            var ownEmployeeId = _currentUser.EmployeeId;

            var inCompany = departments.Where(d => d.CompanyId == companyId);

            return GetDepartmentReadScope() switch
            {
                DataScope.Company => inCompany,
                // Own department = the department of the caller's own employee record (an account without one sees nothing).
                DataScope.Own when ownEmployeeId is { } me =>
                    inCompany.Where(d => _dbContext.Employees.Any(e => e.Id == me && e.CompanyId == companyId && e.DepartmentId == d.Id)),
                _ => inCompany.Where(_ => false)
            };
        }

        private IQueryable<Employee> Apply(IQueryable<Employee> employees, DataScope scope)
        {
            // Reading CompanyId throws when there is no resolved current user, so a missing context never widens access.
            var companyId = _currentUser.CompanyId;
            var ownEmployeeId = _currentUser.EmployeeId;

            // Every scope is bounded by the caller's own company first.
            var inCompany = employees.Where(e => e.CompanyId == companyId);

            return scope switch
            {
                DataScope.Company => inCompany,
                DataScope.Team when ownEmployeeId is { } me => inCompany.Where(e => e.Id == me || e.ManagerId == me),
                DataScope.Own when ownEmployeeId is { } me => inCompany.Where(e => e.Id == me),
                _ => inCompany.Where(_ => false)
            };
        }
    }
}
