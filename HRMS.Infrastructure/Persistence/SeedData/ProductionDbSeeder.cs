using System;
using System.Collections.Generic;
using System.Text;
using HRMS.Application.Interfaces;
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

            var tenantDomain = configuration["HRMS_TENANT_DOMAIN"];
            var adminEmail = configuration["HRMS_ADMIN_EMAIL"];
            var adminPassword = configuration["HRMS_ADMIN_PASSWORD"];

            if (string.IsNullOrWhiteSpace(tenantDomain))
            {
                throw new InvalidOperationException("HRMS_TENANT_DOMAIN is not configured.");
            }
            
            if (string.IsNullOrWhiteSpace(adminEmail))
            {
                throw new InvalidOperationException("HRMS_ADMIN_EMAIL is not configured.");
            }

            if (string.IsNullOrWhiteSpace(adminPassword))
            {
                throw new InvalidOperationException("HRMS_ADMIN_PASSWORD is not configured.");
            }

            var company = await dbContext.Companies.FirstOrDefaultAsync(c => c.Domain == tenantDomain);

            if (company == null)
            {
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

            var role = await dbContext.Roles.FirstOrDefaultAsync(r => r.CompanyId == company.Id && r.Name == "Admin");

            if (role == null)
            {
                role = new Role
                {
                    CompanyId = company.Id,
                    Name = "Admin",
                    DisplayName = "Administrator",
                    Permissions = "{}",
                    IsSystem = true
                };
                dbContext.Roles.Add(role);
                await dbContext.SaveChangesAsync();
            }

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
