using System;
using System.Collections.Generic;
using System.Text;
using HRMS.Application.Interfaces;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HRMS.Infrastructure.Common
{
    public class TenantResolver : ITenantResolver
    {
        private readonly HrmsDbContext _dbContext;
        private readonly ITenantContext _tenantContext;

        public TenantResolver(HrmsDbContext dbContext, ITenantContext tenantContext)
        {
            _dbContext = dbContext;
            _tenantContext = tenantContext;
        }

        public async Task<bool> ResolveAsync(string host, CancellationToken cancellationToken=default)
        {
            if(string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            var normalizedHost = host.Trim().ToLowerInvariant();

            var company = await _dbContext.Companies.AsNoTracking().FirstOrDefaultAsync
                (c => c.Domain.ToLower() == normalizedHost, cancellationToken);

            if(company is null || !company.IsActive)
            {
                return false;
            }

            if(_tenantContext is not TenantContext tenantContext)
            {
                throw new InvalidOperationException("The Registerd ITenantContext implementation is Invalid. ");

            }

            tenantContext.SetTenant(company.Id, company.Domain);

            return true;
        }
    }
}
