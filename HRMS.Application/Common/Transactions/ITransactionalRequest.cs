namespace HRMS.Application.Common.Transactions
{
    /// <summary>
    /// Marks a MediatR request (command) that changes data. The pipeline runs its handler inside one database transaction
    /// (see <see cref="Behaviors.TransactionBehavior{TRequest, TResponse}"/>): all writes commit together or not at all.
    /// Requests without this marker - queries, login, logout - are NOT wrapped in a transaction.
    /// </summary>
    public interface ITransactionalRequest
    {
    }

    /// <summary>
    /// A transactional request that must leave an audit trail. It MUST also carry one or more
    /// <see cref="AuditExpectationAttribute"/> declarations naming the audit entries (module + action) it promises to stage.
    /// Before committing, the pipeline checks that every declaration is matched by a distinct entry staged by this command
    /// while it was running (entries staged earlier in the same request do not count), and rolls the whole operation back
    /// otherwise - also when the command declares nothing.
    /// <para>
    /// What this guarantees: an entry with the declared module and action was staged during the command and commits or rolls
    /// back with it. What it does NOT guarantee: that the entry's target, old/new values or company are the right ones, that a
    /// business change really happened, or that no further entries were staged. Those must be covered by the command's own
    /// tests.
    /// </para>
    /// </summary>
    public interface IAuditedRequest : ITransactionalRequest
    {
    }
}
