using HRMS.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Application.Interfaces
{
    public interface IUserRepository
    {
        Task<User?> GetByEmailAsync(string email, Guid companyId, CancellationToken cancellationToken = default);

        Task<User?> GetByIdAsync( Guid userId,Guid companyId, CancellationToken cancellationToken = default);
    
        /// <summary>
        /// Sets only LastLogin for the given user within the given company. It never writes any other user or role column,
        /// so it cannot overwrite concurrent changes to IsActive, PasswordHash or role permissions.
        /// </summary>
        Task UpdateLastLoginAsync(Guid userId, Guid companyId, DateTime utcNow, CancellationToken cancellationToken = default);
    }
}
