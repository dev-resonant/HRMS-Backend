using HRMS.Application.Interfaces;
using MediatR;

namespace HRMS.Application.Features.Departments.Queries.ListDepartments
{
    /// <summary>GET /api/departments (HR: all departments of the company; manager and employee: their own department). Read-only.</summary>
    public sealed record ListDepartmentsQuery : IRequest<IReadOnlyList<DepartmentDto>>;

    public sealed class ListDepartmentsQueryHandler : IRequestHandler<ListDepartmentsQuery, IReadOnlyList<DepartmentDto>>
    {
        private readonly IDepartmentQueries _queries;

        public ListDepartmentsQueryHandler(IDepartmentQueries queries) => _queries = queries;

        public Task<IReadOnlyList<DepartmentDto>> Handle(ListDepartmentsQuery query, CancellationToken cancellationToken)
            => _queries.ListAsync(cancellationToken);
    }
}
