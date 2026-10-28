using HRMS.Domain.Common;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Domain.Entities
{
   public class Company : BaseEntity
    {
        public string Name { get; set; } = string.Empty;

        private string _domain = string.Empty;

        // Always stored trimmed and lowercase (DomainNormalizer). EF Core reads and writes the backing field, so values loaded
        // from the database are not re-normalized and existing rows are never silently rewritten.
        public string Domain
        {
            get => _domain;
            set => _domain = DomainNormalizer.Normalize(value);
        }

        public string? LogoUrl { get; set; }

        public string Timezone { get; set; } = "Asia/Kolkata";

        public bool IsActive { get; set; } = true;

        /// <summary>
        /// TRUE for THE platform company: the one company that holds the platform (vendor) administrators. At most one company may have it
        /// (unique partial index) and it is never set automatically - an authorized operator designates it explicitly. Ordinary companies are
        /// always FALSE. Platform-only operations require the caller to belong to this company (see <c>ICurrentUser.IsPlatform</c>).
        /// </summary>
        public bool IsPlatform { get; set; }

        public ICollection<User> Users { get; set; } = new List<User>();

        public ICollection<Employee> Employees { get; set; } = new List<Employee>();

        public ICollection<Role> Roles { get; set; } = new List<Role>();

        public ICollection<Department> Departments { get; set; } = new List<Department>();
    }
}
