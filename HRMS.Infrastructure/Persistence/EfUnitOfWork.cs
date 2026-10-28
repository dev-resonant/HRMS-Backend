using HRMS.Application.Interfaces;
using HRMS.Infrastructure.Common;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Infrastructure.Persistence
{
    /// <summary>
    /// One explicit PostgreSQL transaction on the request's <see cref="HrmsDbContext"/>. Because the transaction belongs to the
    /// context's connection, every <c>SaveChanges</c> (including the ones inside repositories) and every
    /// <c>ExecuteUpdate/ExecuteDelete</c> made through that context joins it automatically: nothing becomes visible to other
    /// connections until the single commit at the end. The independent failed-login audit uses a different context and
    /// connection, so it is deliberately NOT part of this transaction.
    /// </summary>
    public sealed class EfUnitOfWork : IUnitOfWork
    {
        private readonly HrmsDbContext _dbContext;
        private readonly PostCommitActions _postCommit;

        public EfUnitOfWork(HrmsDbContext dbContext, PostCommitActions postCommit)
        {
            _dbContext = dbContext;
            _postCommit = postCommit;
        }

        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);

            // A transaction is already open (a handler sent another transactional request): join it. The owner commits or
            // rolls back; committing here would end the outer transaction early.
            if (_dbContext.Database.CurrentTransaction is not null)
            {
                return await operation(cancellationToken);
            }

            // The configured provider strategy (non-retrying for Npgsql unless retries are enabled later) is the supported way
            // to run a user-initiated transaction; with retries enabled the whole block, including the handler, would re-run.
            var strategy = _dbContext.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

                try
                {
                    var result = await operation(cancellationToken);

                    // Flush anything still pending (for example a staged audit entry) INSIDE the transaction. Still cancellable:
                    // nothing is committed yet, so a cancellation here rolls everything back.
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    // The handler and the flush succeeded. From here the request's cancellation (a client that disconnected) must
                    // not interrupt the commit: a cancelled COMMIT leaves it unknown whether the data was committed. This does not
                    // remove that uncertainty for connection or server failures during the commit, nor does it bound the commit
                    // time beyond the provider's command timeout.
                    await transaction.CommitAsync(CancellationToken.None);

                    // Committed: now (and only now) run the work that must not happen for a rolled-back operation, such as handing an
                    // invitation to its delivery channel. Failures there are logged and never change the committed result.
                    await _postCommit.RunAsync();

                    return result;
                }
                catch
                {
                    // Rolls back every write of this operation, including repository saves and the audit entry. The rollback
                    // must not be cancellable and must never hide the original exception.
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    catch
                    {
                        // The connection may already be broken; the server rolls back an uncommitted transaction on its own.
                    }

                    // The tracker still believes the rolled-back changes (and staged audit rows) were saved or pending.
                    _dbContext.ChangeTracker.Clear();

                    // Whatever was queued for after the commit belongs to an operation that did not happen.
                    _postCommit.Clear();

                    throw;
                }
                finally
                {
                    // Disposal runs while an exception may be propagating; if it threw it would replace that exception (or turn a
                    // successful commit into an error), so a disposal failure is deliberately ignored. A transaction that was not
                    // committed is discarded by the database when the connection is released.
                    try
                    {
                        await transaction.DisposeAsync();
                    }
                    catch
                    {
                    }
                }
            });
        }
    }
}
