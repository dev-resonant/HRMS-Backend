using HRMS.Application.Interfaces;
using MediatR;

namespace HRMS.Application.Features.Roles.Queries.ListRoles
{
    /// <summary>GET /api/roles (specification: "List all roles and their permissions"). Read-only: not transactional.</summary>
    public sealed record ListRolesQuery : IRequest<IReadOnlyList<RoleDto>>;

    public sealed class ListRolesQueryHandler : IRequestHandler<ListRolesQuery, IReadOnlyList<RoleDto>>
    {
        private readonly IRoleQueries _queries;

        public ListRolesQueryHandler(IRoleQueries queries) => _queries = queries;

        public Task<IReadOnlyList<RoleDto>> Handle(ListRolesQuery query, CancellationToken cancellationToken)
            => _queries.ListAsync(cancellationToken);
    }
}
