using HRMS.Application.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Infrastructure.Common
{
    public class TenantContext : ITenantContext
    {
        private Guid _companyId;
        private string? _domain;
        private bool _isResolved;

        public Guid CompanyId => _companyId;
        public string? Domain => _domain;
        public bool IsResolved => _isResolved;

        public void SetTenant(Guid companyId, string? domain)
        {
            _companyId = companyId;
            _domain = domain;
            _isResolved = true;
        }
    }
}
