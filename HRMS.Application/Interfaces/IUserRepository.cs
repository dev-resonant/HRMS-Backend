using HRMS.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace HRMS.Application.Interfaces
{
    public interface IUserRepository
    {
        Task<User?> GetByEmailAsync(string email, Guid companyId, CancellationToken cancellationToken = default);
    
        Task UpdateAsync(User user, CancellationToken cancellationToken = default);
    }
}
