using System;
using System.Collections.Generic;
using System.Text;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Infrastructure.Persistence.Repositories
{
   public class UserRepository : IUserRepository
    {
        private readonly HrmsDbContext _dbContext;

        public UserRepository(HrmsDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<User?> GetByEmailAsync(string email,Guid companyId,CancellationToken cancellationToken = default)
        {
            return await _dbContext.Users.AsNoTracking().Include(u => u.Role).FirstOrDefaultAsync(u => u.Email == email && u.CompanyId == companyId, cancellationToken);
        }

        public async Task UpdateAsync(User user, CancellationToken cancellationToken = default)
        {
            _dbContext.Users.Update(user);

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

    }
}
