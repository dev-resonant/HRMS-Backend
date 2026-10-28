using System.Text.RegularExpressions;

namespace HRMS.Application.Audit
{
    /// <summary>Module names (audit_log.module, VARCHAR(50)). Add a constant when a module is first wired.</summary>
    public static class AuditModules
    {
        public const string Auth = "auth";
    }

    /// <summary>Action names (audit_log.action, VARCHAR(100)) in the specification's dotted form, e.g. "employee.created".</summary>
    public static class AuditActions
    {
        public const string LoginFailed = "auth.login_failed";
    }

    /// <summary>
    /// What happened, without who or where: the actor, company and IP address are never part of an entry because they
    /// come from trusted server-side context inside <see cref="Interfaces.IAuditWriter"/>. Old/new values are converted to
    /// sanitized JSON snapshots when the entry is created (see <see cref="AuditPayload"/>).
    /// </summary>
    public sealed class AuditEntry
    {
        public const int MaxModuleLength = 50;

        public const int MaxActionLength = 100;

        // lower-case snake_case words; actions are dotted: "employee.created", "punch.in", "auth.login_failed".
        private static readonly Regex ModulePattern = new(@"^[a-z][a-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ActionPattern = new(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public AuditEntry(string module, string action, Guid? targetId = null, object? oldValue = null, object? newValue = null)
        {
            if (string.IsNullOrEmpty(module) || module.Length > MaxModuleLength || !ModulePattern.IsMatch(module))
            {
                throw new ArgumentException($"Audit module must be lower-case snake_case, at most {MaxModuleLength} characters.", nameof(module));
            }

            if (string.IsNullOrEmpty(action) || action.Length > MaxActionLength || !ActionPattern.IsMatch(action))
            {
                throw new ArgumentException($"Audit action must be dotted lower-case snake_case (for example 'employee.created'), at most {MaxActionLength} characters.", nameof(action));
            }

            if (targetId == Guid.Empty)
            {
                throw new ArgumentException("A target id, when given, must not be empty.", nameof(targetId));
            }

            Module = module;
            Action = action;
            TargetId = targetId;
            OldValueJson = AuditPayload.ToJson(oldValue);
            NewValueJson = AuditPayload.ToJson(newValue);
        }

        public string Module { get; }

        public string Action { get; }

        /// <summary>The affected record; null for system-level actions.</summary>
        public Guid? TargetId { get; }

        public string? OldValueJson { get; }

        public string? NewValueJson { get; }
    }
}
