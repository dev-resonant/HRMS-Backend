using HRMS.Application.Features.Departments;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Infrastructure.Departments
{
    /// <summary>
    /// Read-side department access. Visibility comes from <see cref="IDataScopeService"/> (the company, or only the caller's own
    /// department); the lookups for names and counts are bounded by the department's own company.
    /// </summary>
    public sealed class DepartmentQueries : IDepartmentQueries
    {
        private readonly HrmsDbContext _db;
        private readonly IDataScopeService _scope;
        private readonly ICurrentUser _currentUser;

        public DepartmentQueries(HrmsDbContext db, IDataScopeService scope, ICurrentUser currentUser)
        {
            _db = db;
            _scope = scope;
            _currentUser = currentUser;
        }

        private static IQueryable<DepartmentDto> ToDtos(IQueryable<Department> departments) => departments.Select(d => new DepartmentDto
        {
            Id = d.Id,
            Name = d.Name,
            ParentDepartmentId = d.ParentDeptId,
            ParentDepartmentName = d.ParentDepartment != null ? d.ParentDepartment.Name : null,
            HeadEmployeeId = d.HeadEmpId,
            HeadEmployeeName = d.HeadEmployee != null ? d.HeadEmployee.User.Name : null,
            EmployeeCount = d.Employees.Count(e => e.CompanyId == d.CompanyId),
            ChildDepartmentCount = d.ChildDepartments.Count(c => c.CompanyId == d.CompanyId),
            CreatedAt = d.CreatedAt
        });

        public async Task<DepartmentDto?> GetAsync(Guid departmentId, CancellationToken cancellationToken = default)
            => await ToDtos(_scope.ApplyDepartmentReadScope(_db.Departments.AsNoTracking()).Where(d => d.Id == departmentId))
                .FirstOrDefaultAsync(cancellationToken);

        public async Task<DepartmentDto?> GetInCompanyAsync(Guid departmentId, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            return await ToDtos(_db.Departments.AsNoTracking().Where(d => d.CompanyId == companyId && d.Id == departmentId))
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<DepartmentDto>> ListAsync(CancellationToken cancellationToken = default)
            => await ToDtos(_scope.ApplyDepartmentReadScope(_db.Departments.AsNoTracking()))
                .OrderBy(d => d.Name).ThenBy(d => d.Id)
                .ToListAsync(cancellationToken);
    }
}
