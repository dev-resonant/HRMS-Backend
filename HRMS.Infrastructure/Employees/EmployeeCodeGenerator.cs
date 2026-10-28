using HRMS.Application.Interfaces;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Infrastructure.Employees
{
    /// <summary>
    /// Employee codes per the specification: EMP-0001, EMP-0002, ... unique per company.
    /// <para>
    /// Strategy: inside the request's transaction, take a PostgreSQL transaction-scoped advisory lock keyed by the company, then
    /// read the highest numeric EMP-n code of that company and add one. Concurrent creators in the same company queue on the lock
    /// until the holder commits or rolls back, so they see each other's committed codes and can never compute the same number;
    /// a rolled-back creation leaves no gap. No schema change and no retry loop are needed. The unique index
    /// (CompanyId, EmpCode) stays the final integrity guard: if anything ever bypasses this generator, the insert fails and is
    /// reported as a conflict, never as a duplicate.
    /// </para>
    /// </summary>
    public sealed class EmployeeCodeGenerator : IEmployeeCodeGenerator
    {
        private readonly HrmsDbContext _db;
        private readonly ICurrentUser _currentUser;

        public EmployeeCodeGenerator(HrmsDbContext db, ICurrentUser currentUser)
        {
            _db = db;
            _currentUser = currentUser;
        }

        public async Task<string> NextAsync(CancellationToken cancellationToken = default)
        {
            if (_db.Database.CurrentTransaction is null)
            {
                // The advisory lock is released at transaction end; without a transaction it would protect nothing.
                throw new InvalidOperationException("Employee codes must be allocated inside the request's transaction.");
            }

            var companyId = _currentUser.CompanyId;
            var lockKey = BitConverter.ToInt64(companyId.ToByteArray(), 0);

            await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);

            // Codes that do not look like EMP-<digits> (for example imported ones) are ignored when computing the next number.
            var highest = await _db.Database
                .SqlQuery<int>($$"""
                    SELECT COALESCE(MAX(CAST(substring("EmpCode" from 5) AS integer)), 0) AS "Value"
                    FROM "Employees"
                    WHERE "CompanyId" = {{companyId}} AND "EmpCode" ~ '^EMP-[0-9]{1,9}$'
                    """)
                .SingleAsync(cancellationToken);

            return $"EMP-{highest + 1:D4}";
        }
    }
}
