using HRMS.Domain.Common;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Domain.Entities
{
   public class Company : BaseEntity
    {
        public string Name { get; set; } = string.Empty;

        public string Domain {  get; set; } =string.Empty;

        public string? LogoUrl { get; set; }

        public string Timezone { get; set; } = "Asia/Kolkata";

        public bool IsActive { get; set; } = true;

        public ICollection<User> Users { get; set; } = new List<User>();

        public ICollection<Employee> Employees { get; set; } = new List<Employee>();

        public ICollection<Role> Roles { get; set; } = new List<Role>();

        public ICollection<Department> Departments { get; set; } = new List<Department>();
    }
}
