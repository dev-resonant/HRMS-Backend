using HRMS.Domain.Entities;

namespace HRMS.Application.Interfaces
{
    public interface ISessionRepository
    {
        Task AddAsync(
            Session session,
            CancellationToken cancellationToken = default);

        Task<bool> ExistsActiveAsync(
            string tokenHash,
            CancellationToken cancellationToken = default);

        Task DeleteByTokenHashAsync(
            string tokenHash,
            CancellationToken cancellationToken = default);
    }
}