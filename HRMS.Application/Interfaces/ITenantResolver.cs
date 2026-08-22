using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Application.Interfaces
{
   public interface ITenantResolver
    {
        Task<bool> ResolveAsync(string host, CancellationToken cancellationToken = default);
    }
}
