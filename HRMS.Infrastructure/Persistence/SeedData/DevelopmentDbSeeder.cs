using System;
using System.Collections.Generic;
using System.Data.Common;
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
   public static class DevelopmentDbSeeder
    {
        public const string DevDomain = "localhost";
        public const string DevAdminEmail = "admin@localhost";

        public static async Task SeedAsync(HrmsDbContext dbContext,IPasswordHasher passwordHasher,IConfiguration configuration)
        {
            await dbContext.Database.MigrateAsync();

            // Create development company if it does not exist
            var domain = DomainNormalizer.Normalize(DevDomain);

            var company = await dbContext.Companies.FirstOrDefaultAsync(c => c.Domain.ToLower() == domain);

            if (company == null)
            {
                company = new Company
                {
                    Name = "HRMS Development",
                    Domain = domain,
                    LogoUrl = string.Empty,
                    Timezone = "Asia/Kolkata",
                    IsActive = true,
                };

                dbContext.Companies.Add(company);
                await dbContext.SaveChangesAsync();
            }

            // 2. The development administrator is an HR Admin (system role, shared by all companies)

            var role = await SystemRoleSeeder.EnsureAsync(dbContext, SystemRoleKeys.HrAdmin);

            // 3. Create Development admin user if it does not exist

            var adminEmail = EmailNormalizer.Normalize(DevAdminEmail);

            var user = await dbContext.Users.FirstOrDefaultAsync(u => u.CompanyId == company.Id && u.Email == adminEmail);

            if (user == null)
            {
                var password = configuration["HRMS_DEV_ADMIN_PASSWORD"];

                if (string.IsNullOrWhiteSpace(password))
                {
                    throw new InvalidOperationException("HRMS_DEV_ADMIN_PASSWORD environment variable is not configured.");
                }

                user = new User
                {
                    CompanyId = company.Id,
                    RoleId = role.Id,
                    Name = "Development Administrator",
                    Email = adminEmail,
                    PasswordHash = passwordHasher.Hash(password),
                    IsActive = true,
                    LastLogin = null
                };

                dbContext.Users.Add(user);
                await dbContext.SaveChangesAsync();
            }
        }
    }
}
