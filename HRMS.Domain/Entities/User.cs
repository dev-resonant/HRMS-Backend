using HRMS.Domain.Common;
using HRMS.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Domain.Entities
{
    public class User : BaseEntity
    {
        public Guid CompanyId { get; set; }

        public Guid RoleId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string PasswordHash { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;

        public DateTime? LastLogin { get; set; }

        public Company Company { get; set; } = null!;

        public Role Role { get; set; } = null!;

        public Employee? Employee { get; set; }

        public ICollection<Session> Sessions { get; set; } = new List<Session>();

        public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
    }
}
