using HRMS.Application.Audit;

namespace HRMS.Application.Interfaces
{
    /// <summary>
    /// Append-only audit trail (specification Table 7). There is deliberately no update or delete operation.
    /// Actor, company and IP address are taken from trusted server-side context only - never from the request body.
    /// </summary>
    public interface IAuditWriter
    {
        /// <summary>
        /// Adds an entry to the CURRENT unit of work (the request's database context) for the authenticated caller. It is
        /// persisted by the same <c>SaveChanges</c> / transaction as the business change it describes, so the audit row
        /// exists if and only if that change was committed; if the change fails or is rolled back, so is the entry.
        /// Call it before the save that persists the change. Fails closed (throws) when there is no resolved current user.
        /// </summary>
        void Stage(AuditEntry entry);

        /// <summary>
        /// Records a rejected login for an account that EXISTS (specification section 4, step 3: "log failed attempt with
        /// IP"). It is written immediately in its own transaction, because there is no business change to join and the entry
        /// must survive the failed request. <paramref name="userId"/> and <paramref name="companyId"/> must come from the
        /// server's own lookup of the account within the resolved tenant, never from client input. Attempts against emails
        /// that do not exist cannot be stored (audit_log.user_id is a required foreign key) and are not recorded.
        /// </summary>
        Task RecordFailedLoginAsync(Guid userId, Guid companyId, CancellationToken cancellationToken = default);
    }
}
