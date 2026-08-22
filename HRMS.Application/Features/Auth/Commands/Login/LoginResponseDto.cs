using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Application.Features.Auth.Commands.Login
{
    public class LoginResponseDto
    {
        public string Token { get; set; } = string.Empty;

        public DateTime ExpiresAt { get; set; }

        public UserInfoDto User { get; set; } = null!;
    }

    public class UserInfoDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string Role {  get; set; } = string.Empty;

        public Guid CompanyId { get; set; }
    }
}
