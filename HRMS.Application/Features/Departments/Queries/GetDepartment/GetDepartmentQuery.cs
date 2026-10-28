using HRMS.Application.Common.Exceptions;
using HRMS.Application.Interfaces;
using MediatR;

namespace HRMS.Application.Features.Departments.Queries.GetDepartment
{
    /// <summary>GET /api/departments/{id}. Read-only: not transactional.</summary>
    public sealed record GetDepartmentQuery(Guid DepartmentId) : IRequest<DepartmentDto>;

    public sealed class GetDepartmentQueryHandler : IRequestHandler<GetDepartmentQuery, DepartmentDto>
    {
        private readonly IDepartmentQueries _queries;

        public GetDepartmentQueryHandler(IDepartmentQueries queries) => _queries = queries;

        // Scope-bound lookup: missing, other-company and out-of-scope departments all look the same (404).
        public async Task<DepartmentDto> Handle(GetDepartmentQuery query, CancellationToken cancellationToken)
            => await _queries.GetAsync(query.DepartmentId, cancellationToken) ?? throw new NotFoundException(DepartmentRules.NotFoundMessage);
    }
}
