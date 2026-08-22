using HRMS.Domain.Common;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Domain.Entities
{
    public class AuditLog : BaseEntity
    {
        public Guid UserId { get; set; }

        public Guid CompanyId { get; set; }

        public string Module { get; set; } = string.Empty;

        public string Action {  get; set; } = string.Empty;

        public Guid? TargetId { get; set; }

        public string? OldValue { get; set; }

        public string? NewValue { get; set; }

        public string? IpAddress {  get; set; }

        public User User { get; set; } = null!;

        public Company Company { get; set; } = null!;
    }
}
