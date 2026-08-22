using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Application.Interfaces
{
    public interface IJwtTokenService
    {
        (string Token, DateTime ExpiresAt) GenerateToken(Guid userId, Guid companyId, string email, string role);
    }
}
