using HRMS.Domain.Common;
using HRMS.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Domain.Entities
{
    public class Employee : BaseEntity
    {
        public Guid UserId { get; set; }

        public Guid CompanyId { get; set; }

        public Guid DepartmentId { get; set; }

        public Guid? ManagerId { get; set; }

        public string EmpCode { get; set; } = string.Empty;

        public string Designation { get; set; } = string.Empty;

        public EmployeeStatus Status { get; set; } = EmployeeStatus.Active;

        public User User { get; set; } = null!;

        public Company Company { get; set; } = null!;

        public Department Department { get; set; } = null!;

        public Employee? Manager {  get; set; }

        public ICollection<Employee> DirectReports { get; set; } = new List<Employee>();
    }
}
