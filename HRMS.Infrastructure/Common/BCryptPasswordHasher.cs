using HRMS.Application.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Infrastructure.Common
{
   public class BCryptPasswordHasher : IPasswordHasher
    {
        // Specification (Table 3, users.password_hash): bcrypt cost factor 12 or higher.
        // Verify() reads the cost from each stored hash, so existing hashes created with a lower cost (11) still verify.
        public const int WorkFactor = 12;

        public bool Verify(string password, string passwordHash)
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }

        public string Hash(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
        }
    }
}
