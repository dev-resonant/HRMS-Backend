using System.Collections.Concurrent;
using System.Reflection;
using HRMS.Application.Audit;

namespace HRMS.Application.Common.Transactions
{
    /// <summary>One audit entry (module + action) an audited command promises to stage.</summary>
    public sealed record AuditExpectation(string Module, string Action);

    /// <summary>
    /// Declares the audit entry an <see cref="IAuditedRequest"/> must stage, for example
    /// <c>[AuditExpectation("employee", "employee.created")]</c>. Repeat the attribute for a command that must stage several
    /// entries: every declaration has to be matched by its own staged entry (one entry never satisfies two declarations).
    /// Names are validated with the same rules as <see cref="AuditEntry"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class AuditExpectationAttribute : Attribute
    {
        private static readonly ConcurrentDictionary<Type, IReadOnlyList<AuditExpectation>> Cache = new();

        public AuditExpectationAttribute(string module, string action)
        {
            // Reuses the AuditEntry name rules (lower snake_case module, dotted action, length limits); throws if invalid.
            _ = new AuditEntry(module, action);

            Module = module;
            Action = action;
        }

        public string Module { get; }

        public string Action { get; }

        /// <summary>The declarations on a request type; empty when there are none.</summary>
        public static IReadOnlyList<AuditExpectation> For(Type requestType)
            => Cache.GetOrAdd(requestType, type => type
                .GetCustomAttributes<AuditExpectationAttribute>(inherit: false)
                .Select(a => new AuditExpectation(a.Module, a.Action))
                .ToList());
    }
}
