namespace HRMS.Application.Interfaces
{
    /// <summary>
    /// Runs an operation inside ONE explicit database transaction on the request's database context. Normally handlers do
    /// not call this; they implement <see cref="Common.Transactions.ITransactionalRequest"/> and the pipeline does.
    /// Semantics: everything the operation writes (SaveChanges calls in repositories, bulk updates, staged audit entries) joins the
    /// transaction and becomes visible to other connections only when it commits, once, at the end. If the operation throws, is
    /// cancelled, or the commit fails, everything is rolled back and the context's change tracker is cleared. If a transaction is
    /// already open the operation joins it and never commits or rolls it back itself (no nested transactions).
    /// Cancellation can interrupt the operation and the final flush, but not the commit itself: once the flush has succeeded the
    /// commit runs to completion (it does not remove the uncertainty of a connection or server failure during the commit).
    /// </summary>
    public interface IUnitOfWork
    {
        Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
    }

    /// <summary>The module, action and target of one audit entry staged through <see cref="IAuditWriter.Stage"/>.</summary>
    public sealed record StagedAudit(string Module, string Action, Guid? TargetId);

    /// <summary>
    /// Every audit entry staged so far on the current request's unit of work, in order. The list only grows, so a position taken
    /// before a command runs identifies exactly the entries staged by that command (and anything it called).
    /// </summary>
    public interface IAuditStagingState
    {
        IReadOnlyList<StagedAudit> Staged { get; }
    }
}
