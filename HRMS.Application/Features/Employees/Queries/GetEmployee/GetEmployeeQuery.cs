using HRMS.Application.Common.Exceptions;
using HRMS.Application.Interfaces;
using MediatR;

namespace HRMS.Application.Features.Employees.Queries.GetEmployee
{
    /// <summary>GET /api/employees/{id}. Read-only: not transactional.</summary>
    public sealed record GetEmployeeQuery(Guid EmployeeId) : IRequest<EmployeeDto>;

    public sealed class GetEmployeeQueryHandler : IRequestHandler<GetEmployeeQuery, EmployeeDto>
    {
        private readonly IEmployeeQueries _queries;

        public GetEmployeeQueryHandler(IEmployeeQueries queries) => _queries = queries;

        public async Task<EmployeeDto> Handle(GetEmployeeQuery query, CancellationToken cancellationToken)
        {
            // Scope-bound lookup: missing, other-company and out-of-scope employees all look the same (404).
            return await _queries.GetAsync(query.EmployeeId, cancellationToken)
                ?? throw new NotFoundException(EmployeeRules.NotFoundMessage);
        }
    }
}
