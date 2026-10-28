using FluentValidation;
using HRMS.Application.Interfaces;
using MediatR;

namespace HRMS.Application.Features.Employees.Queries.ListEmployees
{
    /// <summary>GET /api/employees. Read-only: not transactional.</summary>
    public sealed class ListEmployeesQuery : IRequest<PagedResult<EmployeeDto>>
    {
        public const int DefaultPageSize = 20;
        public const int MaxPageSize = 100;
        public const int MaxSearchLength = 100;

        /// <summary>The only values accepted for <see cref="SortBy"/> (case-insensitive).</summary>
        public static readonly IReadOnlyList<string> SortFields = new[] { "empCode", "name", "designation", "joiningDate", "status", "createdAt" };

        /// <summary>1-based.</summary>
        public int Page { get; set; } = 1;

        /// <summary>1 to 100; default 20.</summary>
        public int PageSize { get; set; } = DefaultPageSize;

        /// <summary>Case-insensitive "contains" over name, employee code, designation and email.</summary>
        public string? Search { get; set; }

        /// <summary>Filter: active | inactive | probation | notice_period | contract.</summary>
        public string? Status { get; set; }

        public Guid? DepartmentId { get; set; }

        /// <summary>empCode (default) | name | designation | joiningDate | status | createdAt.</summary>
        public string SortBy { get; set; } = "empCode";

        /// <summary>asc (default) | desc.</summary>
        public string SortDirection { get; set; } = "asc";
    }

    public sealed class ListEmployeesQueryValidator : AbstractValidator<ListEmployeesQuery>
    {
        public ListEmployeesQueryValidator()
        {
            RuleFor(q => q.Page).GreaterThanOrEqualTo(1);

            RuleFor(q => q.PageSize).InclusiveBetween(1, ListEmployeesQuery.MaxPageSize);

            RuleFor(q => q.Search).MaximumLength(ListEmployeesQuery.MaxSearchLength).NoNulCharacters();

            RuleFor(q => q.Status)
                .Must(s => EmployeeStatusNames.TryParse(s, out _))
                .When(q => !string.IsNullOrWhiteSpace(q.Status))
                .WithMessage("Status must be one of: active, inactive, probation, notice_period, contract.");

            RuleFor(q => q.DepartmentId).NotEqual(Guid.Empty).When(q => q.DepartmentId.HasValue);

            RuleFor(q => q.SortBy)
                .Must(s => ListEmployeesQuery.SortFields.Contains(s, StringComparer.OrdinalIgnoreCase))
                .WithMessage("SortBy must be one of: " + string.Join(", ", ListEmployeesQuery.SortFields) + ".");

            RuleFor(q => q.SortDirection)
                .Must(d => string.Equals(d, "asc", StringComparison.OrdinalIgnoreCase) || string.Equals(d, "desc", StringComparison.OrdinalIgnoreCase))
                .WithMessage("SortDirection must be asc or desc.");
        }
    }

    public sealed class ListEmployeesQueryHandler : IRequestHandler<ListEmployeesQuery, PagedResult<EmployeeDto>>
    {
        private readonly IEmployeeQueries _queries;

        public ListEmployeesQueryHandler(IEmployeeQueries queries) => _queries = queries;

        public Task<PagedResult<EmployeeDto>> Handle(ListEmployeesQuery query, CancellationToken cancellationToken)
            => _queries.ListAsync(query, cancellationToken);
    }
}
