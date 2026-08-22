using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Application.Interfaces
{
    public interface ITenantContext
    {
        Guid CompanyId { get; }

        string? Domain { get; }

        bool IsResolved { get; }
    }
}
