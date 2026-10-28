using System;
using System.Collections.Generic;
using System.Text;
using HRMS.Application.Interfaces;
using HRMS.Domain.Common;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HRMS.Infrastructure.Common
{
    public class TenantResolver : ITenantResolver
    {
        private readonly HrmsDbContext _dbContext;
        private readonly ITenantContext _tenantContext;
        private readonly ILogger<TenantResolver> _logger;

        public TenantResolver(HrmsDbContext dbContext, ITenantContext tenantContext, ILogger<TenantResolver> logger)
        {
            _dbContext = dbContext;
            _tenantContext = tenantContext;
            _logger = logger;
        }

        public async Task<bool> ResolveAsync(string host, CancellationToken cancellationToken=default)
        {
            if(string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            var normalizedHost = DomainNormalizer.Normalize(host);

            // Take(2): one row is the normal case; two rows mean domains that differ only by case/whitespace exist, and picking
            // either would be arbitrary - possibly serving (or logging a user into) the wrong tenant. Fail closed instead.
            var matches = await _dbContext.Companies.AsNoTracking()
                .Where(c => c.Domain.ToLower() == normalizedHost)
                .Take(2)
                .ToListAsync(cancellationToken);

            if (matches.Count > 1)
            {
                // The host equals a stored company domain here (it matched), so it is safe to log; company ids are not secrets.
                _logger.LogError(
                    "Tenant resolution is ambiguous: more than one company matches host {Host} (company ids: {CompanyIds}). " +
                    "The request is rejected as an unknown tenant. Resolve the duplicate company domains.",
                    normalizedHost,
                    string.Join(", ", matches.Select(m => m.Id)));

                return false;
            }

            var company = matches.SingleOrDefault();

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
