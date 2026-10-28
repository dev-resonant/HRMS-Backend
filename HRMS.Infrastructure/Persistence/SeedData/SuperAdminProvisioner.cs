using System.Net.Mail;
using HRMS.Application.Features.Auth;
using HRMS.Application.Interfaces;
using HRMS.Domain.Authorization;
using HRMS.Domain.Common;
using HRMS.Domain.Entities;
using HRMS.Infrastructure.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HRMS.Infrastructure.Persistence.SeedData
{
    public enum SuperAdminProvisioningOutcome
    {
        /// <summary>None of the settings is present: the feature is off and nothing was read or written.</summary>
        NotConfigured,

        /// <summary>The account was created.</summary>
        Created,

        /// <summary>The configured account already is the Super Admin: nothing changed.</summary>
        AlreadyProvisioned,

        /// <summary>A different Super Admin already exists: the settings were ignored and nothing changed.</summary>
        ExistingSuperAdminKept
    }

    /// <summary>
    /// One-time, explicitly configured bootstrap of the first Super Admin (specification section 2: "Typically 1-2 users only"; the
    /// specification defines no provisioning workflow). Runs at start-up after the seeders and does nothing unless configured.
    /// <para>
    /// Settings (all three required together, otherwise start-up stops with a message that names the missing setting):
    /// <c>HRMS_SUPERADMIN_EMAIL</c>, <c>HRMS_SUPERADMIN_PASSWORD</c> and <c>HRMS_SUPERADMIN_TENANT_DOMAIN</c> (the EXISTING company the account
    /// belongs to - a user always belongs to one company and signs in on that company's host, and for a Super Admin it MUST be the designated
    /// platform company, <c>Companies.IsPlatform</c>); optional <c>HRMS_SUPERADMIN_NAME</c>.
    /// </para>
    /// <para>
    /// Guarantees: there is no default password (the password must be supplied and meets the normal password policy); the account is created
    /// with the first-login flag, so the configured password must be changed at the first sign-in; nothing is created while ANY Super Admin
    /// exists (so reruns do nothing and a deactivated Super Admin is not resurrected by a leftover setting); an existing account with the
    /// configured e-mail is never promoted; no company is created; nothing existing is modified; the work is one transaction under an
    /// advisory lock, so concurrent starts create at most one account; neither the password nor the e-mail is logged or put in an error.
    /// </para>
    /// <para>
    /// Placement: a Super Admin is only ever created in the designated platform company. This class NEVER creates or designates that company
    /// (an authorized operator does, explicitly - see docs/PLATFORM_ADMINISTRATION.md); if the configured company is not it, or none is
    /// designated, start-up stops and nothing is created. Platform privileges are never inferred from the role name: the platform gate
    /// (<c>[PlatformOnly]</c>) separately requires the platform company AND the permission, read from the database.
    /// </para>
    /// </summary>
    public static class SuperAdminProvisioner
    {
        public const string EmailSetting = "HRMS_SUPERADMIN_EMAIL";
        public const string PasswordSetting = "HRMS_SUPERADMIN_PASSWORD";
        public const string TenantDomainSetting = "HRMS_SUPERADMIN_TENANT_DOMAIN";
        public const string NameSetting = "HRMS_SUPERADMIN_NAME";

        public const string DefaultName = "Super Administrator";

        /// <summary>Recorded in SecurityEvents (user = the new account): the privileged bootstrap has no acting user for the audit log.</summary>
        public const string EventType = "platform.super_admin_provisioned";

        public const string EventReason = "bootstrap_configuration";

        private const int MaxEmailLength = 255;
        private const int MaxNameLength = 100;

        // Two-key advisory lock namespace ("SADM"); independent of the employee-code and department-hierarchy locks.
        private const int LockNamespace = 0x5341444D;
        private const int LockKey = 1;

        public static async Task<SuperAdminProvisioningOutcome> ProvisionAsync(
            HrmsDbContext dbContext,
            IPasswordHasher passwordHasher,
            IConfiguration configuration,
            ILogger logger,
            CancellationToken cancellationToken = default)
        {
            var emailSetting = Value(configuration, EmailSetting);
            var passwordSetting = configuration[PasswordSetting];
            var domainSetting = Value(configuration, TenantDomainSetting);
            var passwordPresent = !string.IsNullOrWhiteSpace(passwordSetting);

            if (emailSetting is null && !passwordPresent && domainSetting is null)
            {
                return SuperAdminProvisioningOutcome.NotConfigured;
            }

            var missing = new List<string>();
            if (emailSetting is null) missing.Add(EmailSetting);
            if (!passwordPresent) missing.Add(PasswordSetting);
            if (domainSetting is null) missing.Add(TenantDomainSetting);

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Super Admin provisioning is partly configured; these settings are missing: {string.Join(", ", missing)}. " +
                    "Set all of them or none. Nothing was created.");
            }

            var email = ValidateEmail(emailSetting!);
            ValidatePassword(passwordSetting!, email);
            var name = ValidateName(Value(configuration, NameSetting));
            var domain = DomainNormalizer.Normalize(domainSetting!);

            var companies = await dbContext.Companies.AsNoTracking()
                .Where(c => c.Domain.ToLower() == domain).Take(2).ToListAsync(cancellationToken);

            if (companies.Count == 0)
            {
                throw new InvalidOperationException($"{TenantDomainSetting} does not match any existing company. No company is created for a Super Admin; nothing was created.");
            }

            if (companies.Count > 1)
            {
                throw new InvalidOperationException($"{TenantDomainSetting} matches more than one company (domains that differ only by case). Nothing was created.");
            }

            var company = companies[0];

            if (!company.IsActive)
            {
                throw new InvalidOperationException($"The company named by {TenantDomainSetting} is not active. Nothing was created.");
            }

            try
            {
                return await ProvisionInTransactionAsync(dbContext, passwordHasher, logger, company.Id, email, passwordSetting!, name, cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                // Never surfaces the database detail (it can quote the e-mail). Nothing was committed.
                throw new InvalidOperationException("Super Admin provisioning was refused by the database and nothing was changed. Rerun the application.", ex);
            }
        }

        private static async Task<SuperAdminProvisioningOutcome> ProvisionInTransactionAsync(
            HrmsDbContext dbContext, IPasswordHasher passwordHasher, ILogger logger,
            Guid companyId, string email, string password, string name, CancellationToken cancellationToken)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // Concurrent starts queue here; the second one finds the account the first one created and does nothing.
            await dbContext.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockNamespace}, {LockKey})", cancellationToken);

            var role = await SystemRoleSeeder.EnsureAsync(dbContext, SystemRoleKeys.SuperAdmin, cancellationToken);

            if (await dbContext.Users.AnyAsync(u => u.RoleId == role.Id, cancellationToken))
            {
                var isConfiguredOne = await dbContext.Users.AnyAsync(u => u.RoleId == role.Id && u.CompanyId == companyId && u.Email == email, cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                if (isConfiguredOne)
                {
                    logger.LogInformation("Super Admin provisioning: the configured account already exists; nothing to do. The {Setting} setting can be removed.", PasswordSetting);
                    return SuperAdminProvisioningOutcome.AlreadyProvisioned;
                }

                logger.LogWarning("Super Admin provisioning is configured but a different Super Admin already exists, so the settings were ignored and nothing was created.");
                return SuperAdminProvisioningOutcome.ExistingSuperAdminKept;
            }

            // Only the designated platform company may hold a Super Admin. Re-read inside the transaction (the designation could have
            // changed since the company was looked up) and require that it is the one and only designated company.
            var isPlatformCompany = await dbContext.Companies.AsNoTracking().AnyAsync(c => c.Id == companyId && c.IsPlatform, cancellationToken)
                && await dbContext.Companies.AsNoTracking().CountAsync(c => c.IsPlatform, cancellationToken) == 1;

            if (!isPlatformCompany)
            {
                throw new InvalidOperationException(
                    $"The company named by {TenantDomainSetting} is not the designated platform company, so no Super Admin is created in it. " +
                    "A Super Admin belongs only to the platform company, which an authorized operator must designate first (it is never designated automatically). Nothing was created.");
            }

            var existing = await dbContext.Users.AsNoTracking()
                .Where(u => u.CompanyId == companyId && u.Email == email)
                .Select(u => u.Role.Name)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is not null)
            {
                // Never promote an existing account silently. The role key is not a secret; the e-mail is not repeated.
                throw new InvalidOperationException(
                    $"An account with the configured {EmailSetting} already exists in the company (role '{existing}'). It is not promoted to Super Admin; " +
                    "use a different e-mail address. Nothing was changed.");
            }

            var user = new User
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                RoleId = role.Id,
                Name = name,
                Email = email,
                PasswordHash = passwordHasher.Hash(password),
                IsActive = true,
                IsFirstLogin = true            // the configured password is temporary: it must be changed at the first sign-in
            };

            dbContext.Users.Add(user);
            dbContext.SecurityEvents.Add(new SecurityEvent
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                UserId = user.Id,
                EventType = EventType,
                Reason = EventReason
            });

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Super Admin provisioned: user {UserId} in company {CompanyId}. The account must change its password at the first sign-in; remove the {PasswordSetting} setting now.",
                user.Id, companyId, PasswordSetting);

            return SuperAdminProvisioningOutcome.Created;
        }

        private static string? Value(IConfiguration configuration, string key)
            => string.IsNullOrWhiteSpace(configuration[key]) ? null : configuration[key]!.Trim();

        private static string ValidateEmail(string configured)
        {
            var email = EmailNormalizer.Normalize(configured);

            if (email.Length > MaxEmailLength
                || email.Any(char.IsControl)
                || !MailAddress.TryCreate(email, out var parsed)
                || !string.Equals(parsed.Address, email, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{EmailSetting} is not a valid e-mail address. Nothing was created.");
            }

            return email;
        }

        private static void ValidatePassword(string password, string email)
        {
            if (!PasswordPolicy.IsAcceptable(password))
            {
                throw new InvalidOperationException($"{PasswordSetting} does not meet the password policy: it {PasswordPolicy.Description} Nothing was created.");
            }

            if (string.Equals(password, email, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{PasswordSetting} must not be the e-mail address. Nothing was created.");
            }
        }

        private static string ValidateName(string? configured)
        {
            var name = configured ?? DefaultName;

            if (name.Length > MaxNameLength || name.Any(char.IsControl))
            {
                throw new InvalidOperationException($"{NameSetting} is invalid (at most {MaxNameLength} characters, no control characters). Nothing was created.");
            }

            return name;
        }
    }
}
