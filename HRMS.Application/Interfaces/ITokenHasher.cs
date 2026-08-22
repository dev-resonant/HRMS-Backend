using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Application.Interfaces
{
    public interface ITokenHasher
    {
        string Hash(string token);
    }
}
