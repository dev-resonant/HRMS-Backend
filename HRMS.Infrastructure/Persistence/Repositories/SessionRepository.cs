using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Infrastructure.Persistence.Repositories
{
    public class SessionRepository : ISessionRepository
    {
        private readonly HrmsDbContext _dbContext;

        public SessionRepository(HrmsDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task AddAsync(
            Session session,
            CancellationToken cancellationToken = default)
        {
            await _dbContext.Sessions.AddAsync(
                session,
                cancellationToken);

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        public async Task<bool> ExistsActiveAsync(
            string tokenHash,
            CancellationToken cancellationToken = default)
        {
            return await _dbContext.Sessions
                .AnyAsync(
                    s => s.TokenHash == tokenHash &&
                         s.ExpiresAt > DateTime.UtcNow,
                    cancellationToken);
        }

        public async Task DeleteByTokenHashAsync(
            string tokenHash,
            CancellationToken cancellationToken = default)
        {
            var session = await _dbContext.Sessions
                .FirstOrDefaultAsync(
                    s => s.TokenHash == tokenHash,
                    cancellationToken);

            if (session is null)
            {
                return;
            }

            _dbContext.Sessions.Remove(session);

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}