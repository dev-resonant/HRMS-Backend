using HRMS.Application.Interfaces;
using System.Security.Cryptography;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Infrastructure.Common
{
   public class TokenHasher : ITokenHasher
    {
        public string Hash (string token)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));

            return Convert.ToHexString(bytes);
        }
    }
}
