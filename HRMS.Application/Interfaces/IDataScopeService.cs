using HRMS.Domain.Authorization;
using HRMS.Domain.Entities;

namespace HRMS.Application.Interfaces
{
    /// <summary>
    /// Resolves how much employee data the current user may reach (decision D1). Own = the caller's employee record,
    /// Team = own record plus direct reports (Employee.ManagerId), Company = every employee of the caller's company.
    /// Everything is limited to the caller's company; there is no cross-company access (Super Admin platform scope is deferred).
    /// </summary>
    public interface IDataScopeService
    {
        DataScope GetEmployeeReadScope();

        DataScope GetEmployeeWriteScope();

        /// <summary>Restricts a query to the employees the caller may read. Returns an empty query when the scope is None.</summary>
        IQueryable<Employee> ApplyEmployeeReadScope(IQueryable<Employee> employees);

        Task<bool> CanReadEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default);

        Task<bool> CanWriteEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Company when the caller holds departments.view_all, Own (only the caller's own department) when they hold
        /// departments.view_own_department, otherwise None.
        /// </summary>
        DataScope GetDepartmentReadScope();

        /// <summary>Restricts a query to the departments the caller may read, always inside the caller's company. Empty when the scope is None.</summary>
        IQueryable<Department> ApplyDepartmentReadScope(IQueryable<Department> departments);
    }
}
