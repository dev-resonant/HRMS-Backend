using System;
using System.Collections.Generic;
using System.Text;
using HRMS.Domain.Entities;

namespace HRMS.Application.Interfaces
{
    public interface ISessionRepository
    {
        Task AddAsync(Session session, CancellationToken cancellationToken = default);
    }
}
