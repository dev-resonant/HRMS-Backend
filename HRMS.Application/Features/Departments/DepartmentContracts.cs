namespace HRMS.Application.Features.Departments
{
    /// <summary>Department as exposed by the API. Never an EF entity; carries no company id.</summary>
    public sealed class DepartmentDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>The department this one is nested under (departments.parent_dept_id), or null for a top-level department.</summary>
        public Guid? ParentDepartmentId { get; set; }

        public string? ParentDepartmentName { get; set; }

        /// <summary>The employee who leads the department (departments.head_emp_id), or null.</summary>
        public Guid? HeadEmployeeId { get; set; }

        public string? HeadEmployeeName { get; set; }

        /// <summary>Employee records currently assigned to this department, whatever their status (offboarded employees stay assigned).</summary>
        public int EmployeeCount { get; set; }

        /// <summary>Departments nested directly under this one.</summary>
        public int ChildDepartmentCount { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    /// <summary>Body of POST /api/departments. The company always comes from the authenticated caller, never from the body.</summary>
    public sealed class CreateDepartmentRequest
    {
        /// <summary>1 to 100 characters.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Optional parent department of the same company (nesting depth is unlimited).</summary>
        public Guid? ParentDepartmentId { get; set; }

        /// <summary>Optional head: an existing, not inactive employee of the same company.</summary>
        public Guid? HeadEmployeeId { get; set; }
    }

    /// <summary>Body of PUT /api/departments/{id}: replaces name, parent and head. A null parent or head clears it.</summary>
    public sealed class UpdateDepartmentRequest
    {
        public string Name { get; set; } = string.Empty;

        public Guid? ParentDepartmentId { get; set; }

        public Guid? HeadEmployeeId { get; set; }
    }

    /// <summary>Body of POST /api/departments/{id}/reassign-employees; {id} is the source department.</summary>
    public sealed class ReassignEmployeesRequest
    {
        /// <summary>The destination department (same company, different from the source).</summary>
        public Guid ToDepartmentId { get; set; }

        /// <summary>Employees to move: 1 to 500 (<see cref="MaxReassignedEmployees"/>), no duplicates, every one currently in the source department.</summary>
        public List<Guid> EmployeeIds { get; set; } = new();

        public const int MaxReassignedEmployees = 500;
    }

    public sealed class ReassignEmployeesResponse
    {
        public Guid FromDepartmentId { get; set; }

        public Guid ToDepartmentId { get; set; }

        public int ReassignedCount { get; set; }

        public IReadOnlyList<Guid> EmployeeIds { get; set; } = Array.Empty<Guid>();
    }
}
