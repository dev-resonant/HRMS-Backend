using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Application.Interfaces
{
    public interface IPasswordHasher
    {
        bool Verify(string password, string passwordHash);

        string Hash(string password);
    }
}
