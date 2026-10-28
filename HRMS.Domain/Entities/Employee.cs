using HRMS.Domain.Common;
using HRMS.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Domain.Entities
{
    public class Employee : BaseEntity
    {
        /// <summary>
        /// Optimistic-concurrency token (PostgreSQL's built-in <c>xmin</c> system column, so there is no extra column to store).
        /// An UPDATE is only applied if the row is unchanged since it was read; otherwise the save fails and nothing is overwritten.
        /// Never exposed by the API and never set by code.
        /// </summary>
        public uint Version { get; set; }

        public Guid UserId { get; set; }

        public Guid CompanyId { get; set; }

        public Guid DepartmentId { get; set; }

        public Guid? ManagerId { get; set; }

        public string EmpCode { get; set; } = string.Empty;

        public string Designation { get; set; } = string.Empty;

        /// <summary>Private email address of the person (onboarding form, specification 5.1 step 1). Optional; not the login name.</summary>
        public string? PersonalEmail { get; set; }

        /// <summary>Contact phone number (onboarding form). Optional.</summary>
        public string? Phone { get; set; }

        /// <summary>full-time / part-time / contract (onboarding form). Optional; independent of <see cref="Status"/>.</summary>
        public EmploymentType? EmploymentType { get; set; }

        public DateOnly JoiningDate { get; set; }

        public DateOnly? LastWorkingDate { get; set; }

        public EmployeeStatus Status { get; set; } = EmployeeStatus.Active;

        public User User { get; set; } = null!;

        public Company Company { get; set; } = null!;

        public Department Department { get; set; } = null!;

        public Employee? Manager {  get; set; }

        public ICollection<Employee> DirectReports { get; set; } = new List<Employee>();
    }
}
