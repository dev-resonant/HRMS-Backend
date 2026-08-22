using HRMS.Application.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Infrastructure.Common
{
   public class BCryptPasswordHasher : IPasswordHasher
    {
        public bool Verify(string password, string passwordHash)
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }

        public string Hash(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password);
        }
    }
}
