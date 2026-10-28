using FluentValidation;
using HRMS.Application.Audit;
using HRMS.Application.Common.Exceptions;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Features.Auth;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Domain.Common;
using HRMS.Domain.Entities;
using HRMS.Domain.Enums;
using MediatR;

namespace HRMS.Application.Features.Employees.Commands.CreateEmployee
{
    /// <summary>
    /// POST /api/employees (specification 5.1). Creates the login account and the employee record in ONE transaction together
    /// with its audit entry. Company, role, status, employee code and password are never taken from the caller.
    /// </summary>
    [AuditExpectation("employee", "employee.created")]
    public sealed record CreateEmployeeCommand(
        string Name,
        string Email,
        Guid DepartmentId,
        Guid? ManagerId,
        string Designation,
        DateOnly JoiningDate,
        string? PersonalEmail = null,
        string? Phone = null,
        string? EmploymentType = null) : IRequest<EmployeeDto>, IAuditedRequest;

    public sealed class CreateEmployeeCommandValidator : AbstractValidator<CreateEmployeeCommand>
    {
        public CreateEmployeeCommandValidator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(EmployeeRules.MaxNameLength).NoNulCharacters();

            RuleFor(c => c.Email).NotEmpty().MaximumLength(EmployeeRules.MaxEmailLength).EmailAddress().NoNulCharacters();

            RuleFor(c => c.DepartmentId).NotEmpty();

            RuleFor(c => c.ManagerId).NotEqual(Guid.Empty).When(c => c.ManagerId.HasValue);

            RuleFor(c => c.Designation).NotEmpty().MaximumLength(EmployeeRules.MaxDesignationLength).NoNulCharacters();

            RuleFor(c => c.JoiningDate)
                .Must(EmployeeRules.IsPlausibleDate)
                .WithMessage("JoiningDate is required and must be between 1900-01-01 and 2100-12-31.");

            this.AddContactRules(c => c.PersonalEmail, c => c.Phone, c => c.EmploymentType);
        }
    }

    public sealed class CreateEmployeeCommandHandler : IRequestHandler<CreateEmployeeCommand, EmployeeDto>
    {
        private readonly ICurrentUser _currentUser;
        private readonly IEmployeeRepository _employees;
        private readonly IEmployeeQueries _queries;
        private readonly IEmployeeCodeGenerator _codes;
        private readonly IReferenceValidator _references;
        private readonly IPasswordHasher _passwordHasher;
        private readonly ITemporaryPasswordGenerator _passwords;
        private readonly IAuditWriter _audit;
        private readonly AccountInvitationIssuer _invitations;

        public CreateEmployeeCommandHandler(
            ICurrentUser currentUser,
            IEmployeeRepository employees,
            IEmployeeQueries queries,
            IEmployeeCodeGenerator codes,
            IReferenceValidator references,
            IPasswordHasher passwordHasher,
            ITemporaryPasswordGenerator passwords,
            IAuditWriter audit,
            AccountInvitationIssuer invitations)
        {
            _invitations = invitations;
            _currentUser = currentUser;
            _employees = employees;
            _queries = queries;
            _codes = codes;
            _references = references;
            _passwordHasher = passwordHasher;
            _passwords = passwords;
            _audit = audit;
        }

        public async Task<EmployeeDto> Handle(CreateEmployeeCommand command, CancellationToken cancellationToken)
        {
            var companyId = _currentUser.CompanyId;                       // trusted: never from the request
            var email = EmailNormalizer.Normalize(command.Email);

            if (await _employees.EmailExistsAsync(email, cancellationToken))
            {
                throw new ConflictException("An account with this email already exists.");
            }

            await _references.EnsureDepartmentInCompanyAsync(command.DepartmentId, "DepartmentId", cancellationToken);

            if (command.ManagerId is { } managerId)
            {
                await EmployeeRules.EnsureManagerAssignableAsync(_references, _employees, managerId, cancellationToken);
            }

            var roleId = await _employees.GetSystemRoleIdAsync(SystemRoleKeys.Employee, cancellationToken);

            // The account starts with a random placeholder password nobody knows (the column is required): the person gets in through
            // the one-time invitation issued below (POST /api/auth/set-password), never through a password anyone could be told.
            // Hashing (slow by design) happens BEFORE the per-company code lock is taken so the lock is held as briefly as possible.
            var passwordHash = _passwordHasher.Hash(_passwords.Generate());

            var empCode = await _codes.NextAsync(cancellationToken);

            var user = new User
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                RoleId = roleId,                                           // default role: employee
                Name = command.Name.Trim(),
                Email = email,
                PasswordHash = passwordHash,
                IsActive = true,
                IsFirstLogin = true                                        // must reset the password on first login
            };

            var employee = new Employee
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                CompanyId = companyId,
                DepartmentId = command.DepartmentId,
                ManagerId = command.ManagerId,
                EmpCode = empCode,
                Designation = command.Designation.Trim(),
                PersonalEmail = EmployeeRules.CleanOptionalEmail(command.PersonalEmail),
                Phone = EmployeeRules.CleanOptional(command.Phone),
                EmploymentType = EmployeeRules.ParseEmploymentType(command.EmploymentType),
                JoiningDate = command.JoiningDate,
                Status = EmployeeStatus.Active
            };

            _employees.Add(user, employee);

            // One-time, short-lived password-setup token (only its hash is stored). It is handed to the delivery channel after the
            // commit and is never part of the response, the audit entry or any log. The creation is audited as employee.created.
            await _invitations.IssueAsync(user, cancellationToken);

            _audit.Stage(new AuditEntry("employee", "employee.created", employee.Id, oldValue: null, newValue: EmployeeRules.Snapshot(employee, user)));

            await _employees.SaveChangesAsync(cancellationToken);

            return await _queries.GetInCompanyAsync(employee.Id, cancellationToken)
                ?? throw new InvalidOperationException("The employee that was just created could not be read back.");
        }
    }
}
