using HRMS.Domain.Common;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Domain.Entities
{
    public class Role : BaseEntity
    {
        public Guid? CompanyId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string DisplayName {  get; set; } = string.Empty;

        public string Permissions {  get; set; } = "{}";

        public bool IsSystem { get; set; }

        public Company? Company { get; set; }

        public ICollection<User> Users { get; set; } = new List<User>();
    }
}
