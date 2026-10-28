using HRMS.Application.Features.Employees;
using HRMS.Application.Features.Employees.Queries.ListEmployees;
using HRMS.Application.Interfaces;
using HRMS.Domain.Entities;
using HRMS.Domain.Enums;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Infrastructure.Employees
{
    /// <summary>
    /// Read-side employee access. Visibility comes from <see cref="IDataScopeService"/> (own / team / company, always bounded by
    /// the caller's company); sorting uses a fixed whitelist, never client text in an expression.
    /// </summary>
    public sealed class EmployeeQueries : IEmployeeQueries
    {
        private readonly HrmsDbContext _db;
        private readonly IDataScopeService _scope;
        private readonly ICurrentUser _currentUser;

        public EmployeeQueries(HrmsDbContext db, IDataScopeService scope, ICurrentUser currentUser)
        {
            _db = db;
            _scope = scope;
            _currentUser = currentUser;
        }

        private sealed class Row
        {
            public Guid Id { get; set; }
            public string EmpCode { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public Guid DepartmentId { get; set; }
            public string DepartmentName { get; set; } = string.Empty;
            public Guid? ManagerId { get; set; }
            public string? ManagerName { get; set; }
            public string Designation { get; set; } = string.Empty;
            public string? PersonalEmail { get; set; }
            public string? Phone { get; set; }
            public EmploymentType? EmploymentType { get; set; }
            public uint Version { get; set; }
            public DateOnly JoiningDate { get; set; }
            public DateOnly? LastWorkingDate { get; set; }
            public EmployeeStatus Status { get; set; }
            public bool IsActive { get; set; }
            public string Role { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
        }

        private static IQueryable<Row> ToRows(IQueryable<Employee> employees) => employees.Select(e => new Row
        {
            Id = e.Id,
            EmpCode = e.EmpCode,
            Name = e.User.Name,
            Email = e.User.Email,
            DepartmentId = e.DepartmentId,
            DepartmentName = e.Department.Name,
            ManagerId = e.ManagerId,
            ManagerName = e.Manager != null ? e.Manager.User.Name : null,
            Designation = e.Designation,
            PersonalEmail = e.PersonalEmail,
            Phone = e.Phone,
            EmploymentType = e.EmploymentType,
            Version = e.Version,
            JoiningDate = e.JoiningDate,
            LastWorkingDate = e.LastWorkingDate,
            Status = e.Status,
            IsActive = e.User.IsActive,
            Role = e.User.Role.Name,
            CreatedAt = e.CreatedAt
        });

        private static EmployeeDto ToDto(Row r) => new()
        {
            Id = r.Id,
            EmpCode = r.EmpCode,
            Name = r.Name,
            Email = r.Email,
            DepartmentId = r.DepartmentId,
            DepartmentName = r.DepartmentName,
            ManagerId = r.ManagerId,
            ManagerName = r.ManagerName,
            Designation = r.Designation,
            PersonalEmail = r.PersonalEmail,
            Phone = r.Phone,
            EmploymentType = r.EmploymentType is { } employmentType ? EmploymentTypeNames.ToApi(employmentType) : null,
            Version = r.Version,
            JoiningDate = r.JoiningDate,
            LastWorkingDate = r.LastWorkingDate,
            Status = EmployeeStatusNames.ToApi(r.Status),
            IsActive = r.IsActive,
            Role = r.Role,
            CreatedAt = r.CreatedAt
        };

        public async Task<EmployeeDto?> GetAsync(Guid employeeId, CancellationToken cancellationToken = default)
        {
            var query = _scope.ApplyEmployeeReadScope(_db.Employees.AsNoTracking()).Where(e => e.Id == employeeId);

            var row = await ToRows(query).FirstOrDefaultAsync(cancellationToken);

            return row is null ? null : ToDto(row);
        }

        public async Task<EmployeeDto?> GetInCompanyAsync(Guid employeeId, CancellationToken cancellationToken = default)
        {
            var companyId = _currentUser.CompanyId;
            var query = _db.Employees.AsNoTracking().Where(e => e.CompanyId == companyId && e.Id == employeeId);

            var row = await ToRows(query).FirstOrDefaultAsync(cancellationToken);

            return row is null ? null : ToDto(row);
        }

        public async Task<PagedResult<EmployeeDto>> ListAsync(ListEmployeesQuery criteria, CancellationToken cancellationToken = default)
        {
            var query = _scope.ApplyEmployeeReadScope(_db.Employees.AsNoTracking());

            if (EmployeeStatusNames.TryParse(criteria.Status, out var status) && !string.IsNullOrWhiteSpace(criteria.Status))
            {
                query = query.Where(e => e.Status == status);
            }

            if (criteria.DepartmentId is { } departmentId)
            {
                query = query.Where(e => e.DepartmentId == departmentId);
            }

            if (!string.IsNullOrWhiteSpace(criteria.Search))
            {
                var pattern = "%" + EscapeLike(criteria.Search.Trim()) + "%";

                query = query.Where(e =>
                    EF.Functions.ILike(e.User.Name, pattern, "\\")
                    || EF.Functions.ILike(e.EmpCode, pattern, "\\")
                    || EF.Functions.ILike(e.Designation, pattern, "\\")
                    || EF.Functions.ILike(e.User.Email, pattern, "\\"));
            }

            var total = await query.CountAsync(cancellationToken);

            var descending = string.Equals(criteria.SortDirection, "desc", StringComparison.OrdinalIgnoreCase);
            var page = Math.Max(1, criteria.Page);
            var pageSize = Math.Clamp(criteria.PageSize, 1, ListEmployeesQuery.MaxPageSize);

            // Offset in 64-bit: page * pageSize overflows an int for very large page numbers (it used to become a negative OFFSET
            // and a 500). A page past the end is simply empty, without another query.
            var offset = (long)(page - 1) * pageSize;

            var rows = offset >= total
                ? new List<Row>()
                : await ToRows(Sort(query, criteria.SortBy, descending)).Skip((int)offset).Take(pageSize).ToListAsync(cancellationToken);

            return new PagedResult<EmployeeDto>
            {
                Items = rows.Select(ToDto).ToList(),
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                TotalPages = (int)Math.Ceiling(total / (double)pageSize)
            };
        }

        // Only these expressions can ever be ordered by; an unknown field is rejected by the validator and again here.
        private static IQueryable<Employee> Sort(IQueryable<Employee> query, string? sortBy, bool descending)
        {
            IOrderedQueryable<Employee> ordered = (sortBy ?? string.Empty).ToLowerInvariant() switch
            {
                "empcode" => descending
                    ? query.OrderByDescending(e => e.EmpCode.Length).ThenByDescending(e => e.EmpCode)    // EMP-9999 before EMP-10000
                    : query.OrderBy(e => e.EmpCode.Length).ThenBy(e => e.EmpCode),
                "name" => descending ? query.OrderByDescending(e => e.User.Name) : query.OrderBy(e => e.User.Name),
                "designation" => descending ? query.OrderByDescending(e => e.Designation) : query.OrderBy(e => e.Designation),
                "joiningdate" => descending ? query.OrderByDescending(e => e.JoiningDate) : query.OrderBy(e => e.JoiningDate),
                "status" => descending ? query.OrderByDescending(e => e.Status) : query.OrderBy(e => e.Status),
                "createdat" => descending ? query.OrderByDescending(e => e.CreatedAt) : query.OrderBy(e => e.CreatedAt),
                _ => throw new ArgumentOutOfRangeException(nameof(sortBy), "Unsupported sort field.")
            };

            return ordered.ThenBy(e => e.Id);                                                   // stable pages
        }

        // % and _ typed by the user are literal characters, not wildcards.
        private static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
}
