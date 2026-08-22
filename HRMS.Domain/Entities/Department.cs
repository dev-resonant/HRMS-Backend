using HRMS.Domain.Common;
using HRMS.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Domain.Entities
{
    public class Department : BaseEntity
    {
        public Guid CompanyId { get; set; }

        public string Name { get; set; } = string.Empty;

        public Guid? HeadEmpId { get; set; }

        public Guid? ParentDeptId { get; set; }

        public Company Company { get; set; } = null!;

        public Employee? HeadEmployee { get; set; }

        public Department? ParentDepartment { get; set; }

        public ICollection<Department> ChildDepartments { get; set; } = new List<Department>();

        public ICollection<Employee> Employees { get; set; } = new List<Employee>();

    }
}
