using System;
using System.Collections.Generic;
using System.Text;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Domain.Common;
using HRMS.Infrastructure.Authorization;
using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HRMS.Infrastructure.Persistence.SeedData
{
    public static class ProductionDbSeeder
    {
        public static async Task SeedAsync(HrmsDbContext dbContext,IPasswordHasher passwordHasher,IConfiguration configuration)
        {
            await dbContext.Database.MigrateAsync();

            var configuredTenantDomain = configuration["HRMS_TENANT_DOMAIN"];
            var configuredAdminEmail = configuration["HRMS_ADMIN_EMAIL"];
            var adminPassword = configuration["HRMS_ADMIN_PASSWORD"];

            if (string.IsNullOrWhiteSpace(configuredTenantDomain))
            {
                throw new InvalidOperationException("HRMS_TENANT_DOMAIN is not configured.");
            }

            // Same rule as the Company.Domain setter and the tenant resolver: trimmed, lowercase.
            var tenantDomain = DomainNormalizer.Normalize(configuredTenantDomain);

            if (string.IsNullOrWhiteSpace(configuredAdminEmail))
            {
                throw new InvalidOperationException("HRMS_ADMIN_EMAIL is not configured.");
            }

            // Same rule as login and the User.Email setter: trimmed, lowercase. Used for the lookup and for creation.
            var adminEmail = EmailNormalizer.Normalize(configuredAdminEmail);

            if (string.IsNullOrWhiteSpace(adminPassword))
            {
                throw new InvalidOperationException("HRMS_ADMIN_PASSWORD is not configured.");
            }

            // Match case-insensitively on the stored value (a company may have been stored with a different casing before
            // normalization existed). An existing company is used as is: its id and stored domain are never rewritten.
            var matches = await dbContext.Companies.Where(c => c.Domain.ToLower() == tenantDomain).Take(2).ToListAsync();

            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    "HRMS_TENANT_DOMAIN matches more than one existing company (domains that differ only by case). " +
                    "Nothing was created. Resolve the duplicate companies manually before starting the application.");
            }

            var company = matches.SingleOrDefault();

            if (company == null)
            {
                // This seeder only bootstraps the FIRST tenant. If companies already exist and none matches the configured
                // domain, the setting most likely does not name the intended company; creating another tenant (and another
                // administrator) silently would split data between two tenants, so refuse instead.
                if (await dbContext.Companies.AnyAsync())
                {
                    throw new InvalidOperationException(
                        "HRMS_TENANT_DOMAIN does not match any existing company, and companies already exist. " +
                        "Refusing to create another tenant automatically. Correct the setting so it names the existing company's domain " +
                        "(case and surrounding whitespace are ignored), or create the additional company through an explicit procedure.");
                }

                company = new Company
                {
                    Name = "HRMS Production",
                    Domain = tenantDomain,
                    LogoUrl = string.Empty,
                    Timezone = "Asia/Kolkata",
                    IsActive = true
                };

                dbContext.Companies.Add(company);
                await dbContext.SaveChangesAsync();
            }

            // The company administrator is an HR Admin (system role, shared by all companies). Existing legacy "Admin" users were
            // moved to it by the AddSystemRoles migration; nothing existing is rewritten here.
            var role = await SystemRoleSeeder.EnsureAsync(dbContext, SystemRoleKeys.HrAdmin);

            var user = await dbContext.Users.FirstOrDefaultAsync(u => u.CompanyId == company.Id && u.Email == adminEmail);

            if (user == null)
            {
                user = new User
                {
                    CompanyId = company.Id,
                    RoleId = role.Id,
                    Name = "Production Administrator",
                    Email = adminEmail,
                    PasswordHash = passwordHasher.Hash(adminPassword),
                    IsActive = true,
                    LastLogin = null
                };

                dbContext.Users.Add(user);
                await dbContext.SaveChangesAsync();
            }
        }
    }
}
