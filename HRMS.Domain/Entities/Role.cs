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

        /// <summary>
        /// Optimistic-concurrency token (PostgreSQL's built-in <c>xmin</c> system column, so there is no extra column to store). A save that
        /// changes a role fails when another transaction changed the same role since it was read, so two editors cannot silently overwrite
        /// each other's permissions.
        /// </summary>
        public uint Version { get; set; }

        public Company? Company { get; set; }

        public ICollection<User> Users { get; set; } = new List<User>();
    }
}
