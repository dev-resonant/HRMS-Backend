using HRMS.Domain.Enums;

namespace HRMS.Application.Features.Employees
{
    /// <summary>Employee as exposed by the API. Never an EF entity; no company id, password data or internal ids beyond what the UI needs.</summary>
    public sealed class EmployeeDto
    {
        public Guid Id { get; set; }

        /// <summary>Auto-generated, unique per company, e.g. EMP-0042.</summary>
        public string EmpCode { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>Work email; also the login username.</summary>
        public string Email { get; set; } = string.Empty;

        public Guid DepartmentId { get; set; }

        public string DepartmentName { get; set; } = string.Empty;

        public Guid? ManagerId { get; set; }

        public string? ManagerName { get; set; }

        public string Designation { get; set; } = string.Empty;

        public string? PersonalEmail { get; set; }

        public string? Phone { get; set; }

        /// <summary>full_time | part_time | contract (optional).</summary>
        public string? EmploymentType { get; set; }

        public DateOnly JoiningDate { get; set; }

        public DateOnly? LastWorkingDate { get; set; }

        /// <summary>active | inactive | probation | notice_period | contract</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Whether the user account can log in.</summary>
        public bool IsActive { get; set; }

        /// <summary>Stable role key of the account (employee, manager, hr_admin, super_admin).</summary>
        public string Role { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Row version used for the ETag response header and the optional If-Match request header. Deliberately not part of the JSON
        /// body or the OpenAPI schema; it is opaque to clients.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public uint Version { get; set; }
    }

    public sealed class PagedResult<T>
    {
        public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();

        public int Page { get; set; }

        public int PageSize { get; set; }

        public int TotalCount { get; set; }

        public int TotalPages { get; set; }
    }

    public sealed class DeactivateEmployeeResponse
    {
        public EmployeeDto Employee { get; set; } = null!;

        /// <summary>Login sessions that were deleted (every device is logged out).</summary>
        public int SessionsRevoked { get; set; }

        /// <summary>
        /// Non-inactive employees that still report to the deactivated employee. They are NOT changed automatically: the
        /// specification says HR must reassign them (decision D10 on how strictly is open).
        /// </summary>
        public int DirectReportsNeedingReassignment { get; set; }
    }

    /// <summary>Body of POST /api/employees. Company, role, status, employee code and password are decided by the server.</summary>
    public sealed class CreateEmployeeRequest
    {
        /// <summary>Full name (max 100).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Work email (max 255); becomes the login username; unique per company.</summary>
        public string Email { get; set; } = string.Empty;

        public Guid DepartmentId { get; set; }

        /// <summary>Reporting manager (an employee of the same company), or null for top level.</summary>
        public Guid? ManagerId { get; set; }

        /// <summary>Job title (max 100).</summary>
        public string Designation { get; set; } = string.Empty;

        /// <summary>yyyy-MM-dd.</summary>
        public DateOnly JoiningDate { get; set; }

        /// <summary>Optional private email address (max 255).</summary>
        public string? PersonalEmail { get; set; }

        /// <summary>Optional phone number: digits, spaces, ( ) - and a leading +; 5 to 20 characters.</summary>
        public string? Phone { get; set; }

        /// <summary>Optional: full_time | part_time | contract.</summary>
        public string? EmploymentType { get; set; }
    }

    /// <summary>Body of PUT /api/employees/{id}: replaces every field below. Email, employee code, company and role cannot be changed here.</summary>
    public sealed class UpdateEmployeeRequest
    {
        public string Name { get; set; } = string.Empty;

        public Guid DepartmentId { get; set; }

        public Guid? ManagerId { get; set; }

        public string Designation { get; set; } = string.Empty;

        public DateOnly JoiningDate { get; set; }

        /// <summary>active | probation | notice_period | contract (use the deactivate endpoint for inactive).</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Required when status is notice_period; must be null otherwise.</summary>
        public DateOnly? LastWorkingDate { get; set; }

        /// <summary>Optional private email address; null clears it.</summary>
        public string? PersonalEmail { get; set; }

        /// <summary>Optional phone number; null clears it.</summary>
        public string? Phone { get; set; }

        /// <summary>Optional: full_time | part_time | contract; null clears it.</summary>
        public string? EmploymentType { get; set; }
    }

    /// <summary>Answer of POST /api/employees/{id}/invitation. The token itself is never returned.</summary>
    public sealed class InvitationIssuedResponse
    {
        public DateTime ExpiresAt { get; set; }

        /// <summary>False when no delivery channel (email) is configured: the invitation exists but nothing was sent.</summary>
        public bool DeliveryConfigured { get; set; }
    }

    public static class EmploymentTypeNames
    {
        public const string FullTime = "full_time";
        public const string PartTime = "part_time";
        public const string Contract = "contract";

        public static string ToApi(HRMS.Domain.Enums.EmploymentType type) => type switch
        {
            HRMS.Domain.Enums.EmploymentType.FullTime => FullTime,
            HRMS.Domain.Enums.EmploymentType.PartTime => PartTime,
            HRMS.Domain.Enums.EmploymentType.Contract => Contract,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        /// <summary>Case-insensitive. Null or blank means "not given" and is accepted as null; anything else must be one of the three values.</summary>
        public static bool TryParse(string? value, out HRMS.Domain.Enums.EmploymentType? type)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case null:
                case "":
                    type = null;
                    return true;
                case FullTime: type = HRMS.Domain.Enums.EmploymentType.FullTime; return true;
                case PartTime: type = HRMS.Domain.Enums.EmploymentType.PartTime; return true;
                case Contract: type = HRMS.Domain.Enums.EmploymentType.Contract; return true;
                default: type = null; return false;
            }
        }
    }

    public sealed class DeactivateEmployeeRequest
    {
        /// <summary>yyyy-MM-dd; not before the joining date.</summary>
        public DateOnly LastWorkingDate { get; set; }
    }

    public static class EmployeeStatusNames
    {
        public const string Active = "active";
        public const string Inactive = "inactive";
        public const string Probation = "probation";
        public const string NoticePeriod = "notice_period";
        public const string Contract = "contract";

        public static string ToApi(EmployeeStatus status) => status switch
        {
            EmployeeStatus.Active => Active,
            EmployeeStatus.Inactive => Inactive,
            EmployeeStatus.Probation => Probation,
            EmployeeStatus.NoticePeriod => NoticePeriod,
            EmployeeStatus.Contract => Contract,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

        /// <summary>Case-insensitive; accepts only the five specified values.</summary>
        public static bool TryParse(string? value, out EmployeeStatus status)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case Active: status = EmployeeStatus.Active; return true;
                case Inactive: status = EmployeeStatus.Inactive; return true;
                case Probation: status = EmployeeStatus.Probation; return true;
                case NoticePeriod: status = EmployeeStatus.NoticePeriod; return true;
                case Contract: status = EmployeeStatus.Contract; return true;
                default: status = default; return false;
            }
        }
    }
}
