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

        private string _email = string.Empty;

        // Always stored trimmed and lowercase (EmailNormalizer), so no code path can create "Admin@x" next to "admin@x".
        // EF Core reads and writes the backing field, so values loaded from the database are not re-normalized.
        public string Email
        {
            get => _email;
            set => _email = EmailNormalizer.Normalize(value);
        }

        public string PasswordHash { get; set; } = string.Empty;

        public bool IsActive { get; set; } = true;

        // TRUE forces a password reset on next login. Employee onboarding sets this to true.
        public bool IsFirstLogin { get; set; }

        public DateTime? LastLogin { get; set; }

        public Company Company { get; set; } = null!;

        public Role Role { get; set; } = null!;

        public Employee? Employee { get; set; }

        public ICollection<Session> Sessions { get; set; } = new List<Session>();

        public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
    }
}
