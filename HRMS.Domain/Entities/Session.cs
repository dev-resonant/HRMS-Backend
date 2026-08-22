using HRMS.Domain.Common;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Domain.Entities
{
    public class Session : BaseEntity
    {
        public Guid UserId { get; set; }

        public string TokenHash { get; set; } = string.Empty;

        public DateTime ExpiresAt { get; set; }

        public string? IpAddress { get; set; }

        public string? UserAgent { get; set; }

        public User User { get; set; } = null!;
    }
}
