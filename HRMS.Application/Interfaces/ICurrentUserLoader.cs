namespace HRMS.Application.Interfaces
{
    public enum CurrentUserLoadResult
    {
        /// <summary>The user, role and employee were read and are consistent; <see cref="ICurrentUser"/> is populated.</summary>
        Loaded,

        /// <summary>No such user in the resolved company (or no resolved tenant / mismatching token company).</summary>
        NotFound,

        /// <summary>The account is deactivated.</summary>
        Inactive,

        /// <summary>The data is inconsistent or ambiguous (for example a role or employee belonging to another company). Fails closed.</summary>
        Denied
    }

    public interface ICurrentUserLoader
    {
        Task<CurrentUserLoadResult> LoadAsync(Guid userId, Guid tokenCompanyId, CancellationToken cancellationToken = default);
    }
}
