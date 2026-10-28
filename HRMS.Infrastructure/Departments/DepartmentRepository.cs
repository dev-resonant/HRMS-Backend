using HRMS.Application.Common.Exceptions;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HRMS.Infrastructure.Departments
{
    /// <summary>Write-side department access. Every query is bound to the authenticated caller's company (ICurrentUser fails closed when unresolved).</summary>
    public sealed class DepartmentRepository : IDepartmentRepository
    {
        // The ancestor walk is bounded; a chain this long is treated as a cycle (fail closed).
        private const int MaxNestingDepth = 1000;

        // First key of the two-key advisory lock. The two-int form lives in a different key space from the single-bigint lock the
        // employee-code generator uses, so the two can never block each other by accident. 0x44455054 = "DEPT".
        private const int HierarchyLockNamespace = 0x44455054;

        private readonly HrmsDbContext _db;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<DepartmentRepository> _logger;

        public DepartmentRepository(HrmsDbContext db, ICurrentUser currentUser, ILogger<DepartmentRepository> logger)
        {
            _db = db;
            _currentUser = currentUser;
            _logger = logger;
        }

        public Task<Department?> GetForUpdateAsync(Guid departmentId, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            return _db.Departments.FirstOrDefaultAsync(d => d.Id == departmentId && d.CompanyId == companyId, cancellationToken);
        }

        public void Add(Department department) => _db.Departments.Add(department);

        public void Remove(Department department) => _db.Departments.Remove(department);

        public Task<int> CountEmployeesAsync(Guid departmentId, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            return _db.Employees.AsNoTracking().CountAsync(e => e.CompanyId == companyId && e.DepartmentId == departmentId, cancellationToken);
        }

        public Task<int> CountChildDepartmentsAsync(Guid departmentId, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            return _db.Departments.AsNoTracking().CountAsync(d => d.CompanyId == companyId && d.ParentDeptId == departmentId, cancellationToken);
        }

        public async Task LockHierarchyAsync(CancellationToken cancellationToken = default)
        {
            if (_db.Database.CurrentTransaction is null)
            {
                // The lock is released at transaction end; without a transaction it would protect nothing.
                throw new InvalidOperationException("The department hierarchy must be locked inside the request's transaction.");
            }

            var companyKey = BitConverter.ToInt32(_currentUser.CompanyId.ToByteArray(), 0);

            await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({HierarchyLockNamespace}, {companyKey})", cancellationToken);
        }

        public async Task<bool> WouldCreateCycleAsync(Guid departmentId, Guid newParentId, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;
            Guid? current = newParentId;

            for (var depth = 0; depth < MaxNestingDepth; depth++)
            {
                if (current is not { } id)
                {
                    return false;                                   // reached a top-level department
                }

                if (id == departmentId)
                {
                    return true;
                }

                current = await _db.Departments.AsNoTracking()
                    .Where(d => d.Id == id && d.CompanyId == companyId)
                    .Select(d => d.ParentDeptId)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            return true;
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The row vanished between the read and the write (another request removed it). Nothing was changed.
                throw new ConflictException("The department was changed by someone else. Reload and try again.");
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                // Constraint name only; never the offending values, never to the caller.
                _logger.LogWarning("A database constraint rejected a department change: {SqlState} {Constraint}.", pg.SqlState, pg.ConstraintName);

                throw new ConflictException("A referenced record no longer exists.");
            }
        }
    }
}
