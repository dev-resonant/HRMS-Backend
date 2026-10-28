using System.Reflection;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Features.Auth.Commands.Login;
using HRMS.Application.Features.Auth.Commands.Logout;
using HRMS.Application.Features.Auth.Queries.Me;
using MediatR;

namespace HRMS.Tests.Transactions;

/// <summary>
/// The command convention for production MediatR requests: every request is either a command or a query, every command
/// declares ITransactionalRequest / IAuditedRequest (with its [AuditExpectation]s) unless it is on the explicit, justified
/// allow-list, and queries never open a transaction. A new request that breaks the rule fails this test.
/// </summary>
public class RequestConventionTests
{
    // The ONLY commands allowed to run without a transaction marker. Each entry states why, based on what the handler does.
    private static readonly IReadOnlyDictionary<Type, string> Exempt = new Dictionary<Type, string>
    {
        [typeof(LoginCommand)] =
            "Authentication, not a business change: inserts a Session row and updates LastLogin through repository calls that save " +
            "immediately (two separate writes - a failure of the second leaves an unused session; making this atomic is a known, " +
            "out-of-scope improvement). It is not audited on success (the specification audits only the failed attempt, which is " +
            "written by a deliberately independent audit path that must not join a surrounding transaction).",
        [typeof(LogoutCommand)] =
            "Authentication, not a business change: a single immediate DELETE of the caller's own Session row, atomic by itself, " +
            "not audited by the specification."
    };

    internal enum RequestKind { Unclassified, Command, Query, Ambiguous }

    internal static RequestKind Classify(Type type)
    {
        var ns = type.Namespace ?? string.Empty;
        var command = type.Name.EndsWith("Command", StringComparison.Ordinal) || ns.Contains(".Commands", StringComparison.Ordinal);
        var query = type.Name.EndsWith("Query", StringComparison.Ordinal) || ns.Contains(".Queries", StringComparison.Ordinal);

        return (command, query) switch
        {
            (true, false) => RequestKind.Command,
            (false, true) => RequestKind.Query,
            (true, true) => RequestKind.Ambiguous,
            _ => RequestKind.Unclassified
        };
    }

    internal static IEnumerable<Type> RequestTypes(Assembly assembly)
        => assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IBaseRequest).IsAssignableFrom(t));

    /// <summary>Returns one message per violation; empty when the convention holds.</summary>
    internal static List<string> Check(IReadOnlyCollection<Type> requests, IReadOnlyDictionary<Type, string> exempt)
    {
        var violations = new List<string>();

        foreach (var type in requests)
        {
            var transactional = typeof(ITransactionalRequest).IsAssignableFrom(type);
            var audited = typeof(IAuditedRequest).IsAssignableFrom(type);
            var kind = Classify(type);

            IReadOnlyList<AuditExpectation> expectations;

            try
            {
                expectations = AuditExpectationAttribute.For(type);
            }
            catch (Exception ex) when (ex is ArgumentException or CustomAttributeFormatException or TypeInitializationException)
            {
                violations.Add($"{type.Name}: has an invalid [AuditExpectation] ({ex.GetType().Name}).");
                continue;
            }

            switch (kind)
            {
                case RequestKind.Unclassified:
                case RequestKind.Ambiguous:
                    violations.Add($"{type.Name}: cannot be classified as a command or a query. Name it *Command or *Query and/or place it in a Commands or Queries namespace (not both).");
                    break;

                case RequestKind.Query when transactional:
                    violations.Add($"{type.Name}: a query must not implement ITransactionalRequest/IAuditedRequest (read-only requests are not wrapped).");
                    break;

                case RequestKind.Command when !transactional && !exempt.ContainsKey(type):
                    violations.Add($"{type.Name}: a command must implement ITransactionalRequest or IAuditedRequest, or be added to the justified allow-list.");
                    break;

                case RequestKind.Command when transactional && exempt.ContainsKey(type):
                    violations.Add($"{type.Name}: is on the allow-list but implements a transaction marker; remove it from the allow-list.");
                    break;
            }

            if (audited && expectations.Count == 0)
            {
                violations.Add($"{type.Name}: an IAuditedRequest must declare at least one [AuditExpectation(module, action)].");
            }

            if (!audited && expectations.Count > 0)
            {
                violations.Add($"{type.Name}: has [AuditExpectation] but does not implement IAuditedRequest, so nothing would enforce it.");
            }
        }

        foreach (var (type, reason) in exempt)
        {
            if (!requests.Contains(type))
            {
                violations.Add($"Allow-list entry {type.Name} no longer exists as a request; remove it.");
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                violations.Add($"Allow-list entry {type.Name} has no documented reason.");
            }
        }

        return violations;
    }

    // ---------- the production assembly ----------

    private static readonly Assembly ApplicationAssembly = typeof(LoginCommand).Assembly;

    [Fact]
    public void Every_production_request_follows_the_command_convention()
    {
        var requests = RequestTypes(ApplicationAssembly).ToList();
        Assert.NotEmpty(requests);

        var violations = Check(requests, Exempt);

        Assert.True(violations.Count == 0, "Convention violations:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void The_production_requests_are_classified_as_expected()
    {
        var kinds = RequestTypes(ApplicationAssembly).ToDictionary(t => t, Classify);

        Assert.Equal(RequestKind.Command, kinds[typeof(LoginCommand)]);
        Assert.Equal(RequestKind.Command, kinds[typeof(LogoutCommand)]);
        Assert.Equal(RequestKind.Query, kinds[typeof(MeQuery)]);
    }

    [Fact]
    public void Login_and_logout_are_the_only_exemptions_and_both_stay_unwrapped()
    {
        Assert.Equal(new[] { typeof(LoginCommand), typeof(LogoutCommand) }.OrderBy(t => t.Name), Exempt.Keys.OrderBy(t => t.Name));
        Assert.All(Exempt.Keys, t => Assert.False(typeof(ITransactionalRequest).IsAssignableFrom(t)));
        Assert.False(typeof(ITransactionalRequest).IsAssignableFrom(typeof(MeQuery)));          // queries stay unwrapped
        Assert.All(Exempt.Values, reason => Assert.True(reason.Length > 60));                   // a real justification, not a placeholder
    }

    [Fact]
    public void There_are_no_notifications_or_stream_requests_that_the_convention_would_miss()
    {
        var other = ApplicationAssembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && (typeof(INotification).IsAssignableFrom(t) || t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStreamRequest<>))))
            .Select(t => t.Name)
            .ToList();

        Assert.True(other.Count == 0, "Extend the convention to cover: " + string.Join(", ", other));
    }

    // ---------- negative controls: the checker catches future mistakes ----------

    private static List<string> CheckSamples(params Type[] samples) => Check(samples, new Dictionary<Type, string>());

    [Fact]
    public void A_command_without_any_declaration_is_caught()
        => Assert.Contains(CheckSamples(typeof(Samples.SampleUnmarkedCommand)), v => v.Contains("SampleUnmarkedCommand") && v.Contains("must implement ITransactionalRequest"));

    [Fact]
    public void A_query_that_opens_a_transaction_is_caught()
        => Assert.Contains(CheckSamples(typeof(Samples.SampleWrappedQuery)), v => v.Contains("SampleWrappedQuery") && v.Contains("query must not"));

    [Fact]
    public void An_audited_command_without_an_audit_declaration_is_caught()
        => Assert.Contains(CheckSamples(typeof(Samples.SampleAuditedWithoutDeclarationCommand)), v => v.Contains("must declare at least one [AuditExpectation"));

    [Fact]
    public void An_audit_declaration_that_nothing_enforces_is_caught()
        => Assert.Contains(CheckSamples(typeof(Samples.SampleDeclaredButNotAuditedCommand)), v => v.Contains("does not implement IAuditedRequest"));

    [Fact]
    public void A_request_that_is_neither_a_command_nor_a_query_is_caught()
        => Assert.Contains(CheckSamples(typeof(Samples.SampleOddRequest)), v => v.Contains("cannot be classified"));

    [Fact]
    public void A_stale_or_unjustified_allow_list_entry_is_caught()
    {
        var staleAndMarked = Check(
            new[] { typeof(Samples.SampleMarkedCommand) },
            new Dictionary<Type, string> { [typeof(Samples.SampleMarkedCommand)] = "marked and allow-listed", [typeof(Samples.SampleMissingCommand)] = "" });

        Assert.Contains(staleAndMarked, v => v.Contains("implements a transaction marker"));
        Assert.Contains(staleAndMarked, v => v.Contains("SampleMissingCommand") && v.Contains("no longer exists"));
        Assert.Contains(staleAndMarked, v => v.Contains("SampleMissingCommand") && v.Contains("no documented reason"));
    }

    [Fact]
    public void Correctly_declared_requests_produce_no_violation()
    {
        var violations = CheckSamples(typeof(Samples.SampleMarkedCommand), typeof(Samples.SampleAuditedCommand), typeof(Samples.SampleReadQuery));

        Assert.Empty(violations);
    }

    [Fact]
    public void An_allow_listed_command_is_accepted_only_while_it_is_listed()
    {
        var listed = Check(new[] { typeof(Samples.SampleUnmarkedCommand) }, new Dictionary<Type, string> { [typeof(Samples.SampleUnmarkedCommand)] = "justified for this test" });

        Assert.Empty(listed);
        Assert.NotEmpty(CheckSamples(typeof(Samples.SampleUnmarkedCommand)));              // without the entry it fails again
    }
}

public static class Samples
{
    // Test-only request types used as negative/positive controls. They have no handlers and are never sent.
    public sealed record SampleMarkedCommand : IRequest, ITransactionalRequest;

    [AuditExpectation("sample", "sample.created")]
    public sealed record SampleAuditedCommand : IRequest, IAuditedRequest;

    public sealed record SampleReadQuery : IRequest<int>;

    public sealed record SampleUnmarkedCommand : IRequest;

    public sealed record SampleWrappedQuery : IRequest<int>, ITransactionalRequest;

    public sealed record SampleAuditedWithoutDeclarationCommand : IRequest, IAuditedRequest;

    [AuditExpectation("sample", "sample.created")]
    public sealed record SampleDeclaredButNotAuditedCommand : IRequest, ITransactionalRequest;

    public sealed record SampleOddRequest : IRequest;

    public sealed record SampleMissingCommand : IRequest;
}
