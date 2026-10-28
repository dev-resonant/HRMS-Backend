using System.Security.Cryptography;
using FluentValidation;
using FluentValidation.Results;
using HRMS.Application.Interfaces;
using HRMS.Domain.Common;
using HRMS.Domain.Entities;
using HRMS.Domain.Enums;

namespace HRMS.Application.Features.Employees
{
    /// <summary>Rules and helpers shared by the employee commands.</summary>
    public static class EmployeeRules
    {
        public const int MaxNameLength = 100;
        public const int MaxEmailLength = 255;
        public const int MaxDesignationLength = 100;

        // Sanity bounds for dates (assumption, see docs): the specification only says "DATE".
        public static readonly DateOnly MinDate = new(1900, 1, 1);
        public static readonly DateOnly MaxDate = new(2100, 12, 31);

        public const string NotFoundMessage = "Employee not found.";

        public static bool IsPlausibleDate(DateOnly date) => date >= MinDate && date <= MaxDate;

        public static ValidationException Invalid(string property, string message)
            => new(new[] { new ValidationFailure(property, message) });

        /// <summary>
        /// A manager must be an employee of the caller's company (same message whether missing or foreign) and must not be
        /// inactive. (The "not inactive" rule is an assumption, see docs.)
        /// </summary>
        public static async Task EnsureManagerAssignableAsync(
            IReferenceValidator references, IEmployeeRepository employees, Guid managerId, CancellationToken cancellationToken,
            bool requireActive = true)
        {
            await references.EnsureEmployeeInCompanyAsync(managerId, "ManagerId", cancellationToken);

            // requireActive is false when the employee keeps the manager they already have: if that manager was deactivated since,
            // unrelated edits must not be blocked (HR reassigns the report when it chooses to).
            if (requireActive && await employees.GetStatusAsync(managerId, cancellationToken) == EmployeeStatus.Inactive)
            {
                throw Invalid("ManagerId", "ManagerId must refer to an active employee.");
            }
        }

        public const int MaxPhoneLength = 20;

        // Digits, spaces, parentheses and hyphens with an optional leading "+" or "("; 5 to 20 characters. (The specification names the
        // field but no format - assumption, see docs.)
        private static readonly System.Text.RegularExpressions.Regex PhonePattern =
            new(@"^(\+[0-9][0-9 ()\-]{3,17}[0-9)]|\(?[0-9][0-9 ()\-]{3,17}[0-9)])$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Validation of the optional onboarding-form fields shared by create and update.</summary>
        public static void AddContactRules<T>(this AbstractValidator<T> validator,
            System.Linq.Expressions.Expression<Func<T, string?>> personalEmail,
            System.Linq.Expressions.Expression<Func<T, string?>> phone,
            System.Linq.Expressions.Expression<Func<T, string?>> employmentType)
        {
            validator.RuleFor(personalEmail).MaximumLength(MaxEmailLength).EmailAddress().NoNulCharacters()
                .When(c => !string.IsNullOrWhiteSpace(personalEmail.Compile()(c)));

            validator.RuleFor(phone).Must(p => p is null || PhonePattern.IsMatch(p.Trim()))
                .WithMessage("{PropertyName} must be 5 to 20 characters: digits, spaces, ( ) - and an optional leading +.")
                .When(c => !string.IsNullOrWhiteSpace(phone.Compile()(c)));

            validator.RuleFor(employmentType).Must(t => EmploymentTypeNames.TryParse(t, out _))
                .WithMessage("{PropertyName} must be one of: full_time, part_time, contract.");
        }

        /// <summary>Blank becomes null; otherwise trimmed.</summary>
        public static string? CleanOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>Normalizes an optional private email like the login email (trimmed, lower case).</summary>
        public static string? CleanOptionalEmail(string? value) => CleanOptional(value) is { } email ? EmailNormalizer.Normalize(email) : null;

        public static Domain.Enums.EmploymentType? ParseEmploymentType(string? value)
            => EmploymentTypeNames.TryParse(value, out var type) ? type : null;

        /// <summary>
        /// If-Match precondition (optional, backward compatible): when the client sent one, the employee must still be at one of the
        /// versions it named; otherwise the form it is based on is stale and nothing is changed.
        /// </summary>
        public static void EnsureVersionMatches(Employee employee, IReadOnlyList<uint>? ifMatch)
        {
            if (ifMatch is not null && !ifMatch.Contains(employee.Version))
            {
                throw new Common.Exceptions.PreconditionFailedException("The employee has changed since you loaded it. Reload the employee and try again.");
            }
        }

        /// <summary>PostgreSQL text cannot contain U+0000; it would surface as a 500, so it is rejected as invalid input instead.</summary>
        public static IRuleBuilderOptions<T, string?> NoNulCharacters<T>(this IRuleBuilder<T, string?> rule)
            => rule.Must(value => value is null || !value.Contains('\0')).WithMessage("{PropertyName} contains an invalid character.");

        /// <summary>The audited "full state" of an employee record and its login account. Contains no credential of any kind.</summary>
        public static EmployeeAuditSnapshot Snapshot(Employee employee, User user) => new(
            employee.Id,
            user.Id,
            employee.EmpCode,
            user.Name,
            user.Email,
            employee.DepartmentId,
            employee.ManagerId,
            employee.Designation,
            employee.PersonalEmail,
            employee.Phone,
            employee.EmploymentType is { } employmentType ? EmploymentTypeNames.ToApi(employmentType) : null,
            employee.JoiningDate,
            employee.LastWorkingDate,
            EmployeeStatusNames.ToApi(employee.Status),
            user.IsActive);
    }

    public sealed record EmployeeAuditSnapshot(
        Guid Id,
        Guid UserId,
        string EmpCode,
        string Name,
        string Email,
        Guid DepartmentId,
        Guid? ManagerId,
        string Designation,
        string? PersonalEmail,
        string? Phone,
        string? EmploymentType,
        DateOnly JoiningDate,
        DateOnly? LastWorkingDate,
        string Status,
        bool IsActive);

    /// <summary>
    /// The audited "after" state of a deactivation: exactly the fields of <see cref="EmployeeAuditSnapshot"/> (so before and after
    /// can be compared field by field) plus what the operation did to related data.
    /// </summary>
    public sealed record EmployeeDeactivationAuditSnapshot(
        Guid Id,
        Guid UserId,
        string EmpCode,
        string Name,
        string Email,
        Guid DepartmentId,
        Guid? ManagerId,
        string Designation,
        string? PersonalEmail,
        string? Phone,
        string? EmploymentType,
        DateOnly JoiningDate,
        DateOnly? LastWorkingDate,
        string Status,
        bool IsActive,
        int SessionsRevoked,
        int DirectReportsNeedingReassignment)
    {
        public static EmployeeDeactivationAuditSnapshot From(EmployeeAuditSnapshot s, int sessionsRevoked, int directReports) => new(
            s.Id, s.UserId, s.EmpCode, s.Name, s.Email, s.DepartmentId, s.ManagerId, s.Designation, s.PersonalEmail, s.Phone, s.EmploymentType,
            s.JoiningDate, s.LastWorkingDate, s.Status, s.IsActive, sessionsRevoked, directReports);
    }

    public sealed class TemporaryPasswordGenerator : ITemporaryPasswordGenerator
    {
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        private const int Length = 20;

        /// <summary>Cryptographically random, about 116 bits. Only the BCrypt hash is ever stored.</summary>
        public string Generate()
        {
            var chars = new char[Length];

            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }

            return new string(chars);
        }
    }
}
