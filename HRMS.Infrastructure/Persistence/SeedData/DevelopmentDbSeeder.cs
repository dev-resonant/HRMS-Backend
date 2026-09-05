using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HRMS.Infrastructure.Persistence.SeedData
{
   public static class DevelopmentDbSeeder
    {
        public static async Task SeedAsync(HrmsDbContext dbContext,IPasswordHasher passwordHasher,IConfiguration configuration)
        {
            await dbContext.Database.MigrateAsync();

            // Create development company if it does not exist
            var company = await dbContext.Companies.FirstOrDefaultAsync(c => c.Domain == "localhost");

            if (company == null)
            {
                company = new Company
                {
                    Name = "HRMS Development",
                    Domain = "localhost",
                    LogoUrl = string.Empty,
                    Timezone = "Asia/Kolkata",
                    IsActive = true,
                };

                dbContext.Companies.Add(company);
                await dbContext.SaveChangesAsync();
            }

            // 2. Create Admin role if it does not exist

            var role = await dbContext.Roles.FirstOrDefaultAsync(r => r.CompanyId == company.Id && r.Name == "Admin");

            if (role == null)
            {
                role = new Role
                {
                    CompanyId = company.Id,
                    Name = "Admin",
                    DisplayName = "Administrator",
                    Permissions = "{ }",
                    IsSystem = true
                };

                dbContext.Roles.Add(role);
                await dbContext.SaveChangesAsync();
            }

            // 3. Create Development admin user if it does not exist

            var user = await dbContext.Users.FirstOrDefaultAsync(u => u.CompanyId == company.Id && u.Email == "admin@localhost");

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
                    Email = "admin@localhost",
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
