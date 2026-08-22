using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Infrastructure.Persistence.Repositories
{
    public class SessionRepository : ISessionRepository
    {
        private readonly HrmsDbContext _dbContext;

        public SessionRepository(HrmsDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task AddAsync(Session session, CancellationToken cancellationToken = default)
        {
            await _dbContext.Sessions.AddAsync(session, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

    }
}
