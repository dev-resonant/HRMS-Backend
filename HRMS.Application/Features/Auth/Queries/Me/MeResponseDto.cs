using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HRMS.Application.Features.Auth.Queries.Me
{
    public class MeResponseDto
    {
        public Guid Id { get; set; }
        public Guid CompanyId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        /// <summary>Stable role key (employee, manager, hr_admin, super_admin).</summary>
        public string Role {  get; set; } = string.Empty;

        public string RoleDisplayName { get; set; } = string.Empty;

        public Guid? EmployeeId { get; set; }

        /// <summary>Module -> allowed actions, as a JSON object (not a JSON string).</summary>
        public IReadOnlyDictionary<string, string[]> Permissions { get; set; } = new Dictionary<string, string[]>();
        public bool IsActive { get; set; }

        /// <summary>True while the account must set a password before using anything else (is_first_login).</summary>
        public bool PasswordChangeRequired { get; set; }
    }
}
