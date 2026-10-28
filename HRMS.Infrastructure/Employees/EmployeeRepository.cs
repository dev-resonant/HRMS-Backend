using HRMS.Application.Common.Exceptions;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Domain.Enums;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HRMS.Infrastructure.Employees
{
    /// <summary>Write-side employee access. Every query is bound to the authenticated caller's company (ICurrentUser fails closed when unresolved).</summary>
    public sealed class EmployeeRepository : IEmployeeRepository
    {
        // The reporting-chain walk is bounded; a chain this long is treated as a cycle (fail closed).
        private const int MaxReportingDepth = 1000;

        private readonly HrmsDbContext _db;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<EmployeeRepository> _logger;

        public EmployeeRepository(HrmsDbContext db, ICurrentUser currentUser, ILogger<EmployeeRepository> logger)
        {
            _db = db;
            _currentUser = currentUser;
            _logger = logger;
        }

        public Task<Employee?> GetForUpdateAsync(Guid employeeId, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            return _db.Employees
                .Include(e => e.User)
                .FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId && e.User.CompanyId == companyId, cancellationToken);
        }

        public Task<List<Employee>> GetManyForUpdateAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            return _db.Employees.Where(e => employeeIds.Contains(e.Id) && e.CompanyId == companyId).ToListAsync(cancellationToken);
        }

        public Task<EmployeeStatus?> GetStatusAsync(Guid employeeId, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            return _db.Employees.AsNoTracking()
                .Where(e => e.Id == employeeId && e.CompanyId == companyId)
                .Select(e => (EmployeeStatus?)e.Status)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            return _db.Users.AsNoTracking().AnyAsync(u => u.CompanyId == companyId && u.Email == normalizedEmail, cancellationToken);
        }

        public async Task<Guid> GetSystemRoleIdAsync(string roleKey, CancellationToken cancellationToken = default)
        {
            var id = await _db.Roles.AsNoTracking()
                .Where(r => r.CompanyId == null && r.IsSystem && r.Name == roleKey)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(cancellationToken);

            return id ?? throw new InvalidOperationException($"The built-in role '{roleKey}' does not exist.");
        }

        public void Add(User user, Employee employee)
        {
            _db.Users.Add(user);
            _db.Employees.Add(employee);
        }

        public void MarkChanged(Employee employee)
            => _db.Entry(employee).Property(e => e.Designation).IsModified = true;

        public async Task<bool> WouldCreateReportingCycleAsync(Guid employeeId, Guid newManagerId, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;
            Guid? current = newManagerId;

            for (var depth = 0; depth < MaxReportingDepth; depth++)
            {
                if (current is not { } id)
                {
                    return false;                                   // reached the top of the chain
                }

                if (id == employeeId)
                {
                    return true;
                }

                current = await _db.Employees.AsNoTracking()
                    .Where(e => e.Id == id && e.CompanyId == companyId)
                    .Select(e => e.ManagerId)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            return true;
        }

        public Task<int> CountActiveDirectReportsAsync(Guid managerId, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;

            return _db.Employees.AsNoTracking()
                .CountAsync(e => e.CompanyId == companyId && e.ManagerId == managerId && e.Status != EmployeeStatus.Inactive, cancellationToken);
        }

        public Task<int> RevokeSessionsAsync(Guid userId, CancellationToken cancellationToken = default)
            => _db.Sessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The employee row changed after this request read it (another administrator committed first). Nothing was
                // overwritten; the surrounding transaction rolls back. The caller reloads and retries.
                _logger.LogWarning("An employee change was refused because the row was modified concurrently.");

                throw new ConflictException("The employee was changed by someone else. Reload the employee and try again.");
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && Translate(pg) is { } conflict)
            {
                // Constraint and table names only; never the offending values, never to the caller.
                _logger.LogWarning("A database constraint rejected an employee change: {SqlState} {Constraint}.", pg.SqlState, pg.ConstraintName);

                throw conflict;
            }
        }

        private static ConflictException? Translate(PostgresException pg) => (pg.SqlState, pg.ConstraintName) switch
        {
            (PostgresErrorCodes.UniqueViolation, "IX_Users_CompanyId_Email") => new ConflictException("An account with this email already exists."),
            (PostgresErrorCodes.UniqueViolation, "IX_Employees_CompanyId_EmpCode") => new ConflictException("The employee code is already in use. Please retry."),
            (PostgresErrorCodes.UniqueViolation, _) => new ConflictException("The employee conflicts with an existing record."),
            (PostgresErrorCodes.ForeignKeyViolation, _) => new ConflictException("A referenced record no longer exists."),
            _ => null
        };
    }
}
