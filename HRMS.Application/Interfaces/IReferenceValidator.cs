namespace HRMS.Application.Interfaces
{
    /// <summary>
    /// Rejects references to rows outside the caller's company. A failure throws a validation error that does not reveal
    /// whether the referenced row exists in another company ("does not exist" for both cases).
    /// </summary>
    public interface IReferenceValidator
    {
        Task EnsureEmployeeInCompanyAsync(Guid employeeId, string propertyName, CancellationToken cancellationToken = default);

        Task EnsureDepartmentInCompanyAsync(Guid departmentId, string propertyName, CancellationToken cancellationToken = default);

        Task EnsureUserInCompanyAsync(Guid userId, string propertyName, CancellationToken cancellationToken = default);

        /// <summary>A role is assignable when it is a system role (no company) or belongs to the caller's company.</summary>
        Task EnsureRoleAssignableAsync(Guid roleId, string propertyName, CancellationToken cancellationToken = default);
    }
}
