using FluentValidation;
using HRMS.Application.Features.Employees;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Domain.Enums;

namespace HRMS.Application.Features.Departments
{
    /// <summary>Rules shared by the department commands.</summary>
    public static class DepartmentRules
    {
        /// <summary>departments.name is VARCHAR(100) (Table 5).</summary>
        public const int MaxNameLength = 100;

        /// <summary>One answer for a missing, foreign or out-of-scope department.</summary>
        public const string NotFoundMessage = "Department not found.";

        public static IRuleBuilderOptions<T, string> ValidName<T>(this IRuleBuilder<T, string> rule)
            => rule.Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("Name is required.")
                .MaximumLength(MaxNameLength)
                .Must(name => name is null || !name.Contains('\0')).WithMessage("Name must not contain NUL characters.");

        /// <summary>The audited state of a department: business fields only.</summary>
        public static object Snapshot(Department department) => new
        {
            department.Name,
            department.ParentDeptId,
            department.HeadEmpId
        };

        /// <summary>
        /// The head must be an employee of the caller's company (same message whether missing or foreign) and, unless the department keeps
        /// the head it already has, must not be inactive. (The "not inactive" rule is an assumption, see docs.)
        /// </summary>
        public static async Task EnsureHeadAssignableAsync(
            IReferenceValidator references, IEmployeeRepository employees, Guid headId, bool requireActive, CancellationToken cancellationToken)
        {
            await references.EnsureEmployeeInCompanyAsync(headId, "HeadEmployeeId", cancellationToken);

            if (requireActive && await employees.GetStatusAsync(headId, cancellationToken) == EmployeeStatus.Inactive)
            {
                throw EmployeeRules.Invalid("HeadEmployeeId", "HeadEmployeeId must refer to an active employee.");
            }
        }

        /// <summary>The parent must be a department of the caller's company, not the department itself, and not one of its descendants.</summary>
        public static async Task EnsureParentAssignableAsync(
            IReferenceValidator references, IDepartmentRepository departments, Guid? departmentId, Guid parentId, CancellationToken cancellationToken)
        {
            await references.EnsureDepartmentInCompanyAsync(parentId, "ParentDepartmentId", cancellationToken);

            if (departmentId is { } id && await departments.WouldCreateCycleAsync(id, parentId, cancellationToken))
            {
                throw EmployeeRules.Invalid("ParentDepartmentId", "A department cannot be nested under itself or under one of its own sub-departments.");
            }
        }
    }
}
