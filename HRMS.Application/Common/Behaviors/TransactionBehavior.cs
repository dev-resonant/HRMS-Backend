using HRMS.Application.Common.Transactions;
using HRMS.Application.Interfaces;
using MediatR;

namespace HRMS.Application.Common.Behaviors
{
    /// <summary>
    /// Wraps handlers of <see cref="ITransactionalRequest"/> in one database transaction. Register it AFTER
    /// <see cref="ValidationBehavior{TRequest, TResponse}"/> so that invalid requests are rejected before a transaction is opened.
    /// Any other request passes straight through untouched.
    /// </summary>
    public class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IAuditStagingState _auditState;

        public TransactionBehavior(IUnitOfWork unitOfWork, IAuditStagingState auditState)
        {
            _unitOfWork = unitOfWork;
            _auditState = auditState;
        }

        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            if (request is not ITransactionalRequest)
            {
                return await next(cancellationToken);
            }

            IReadOnlyList<AuditExpectation>? expectations = null;

            if (request is IAuditedRequest)
            {
                expectations = AuditExpectationAttribute.For(typeof(TRequest));

                // Fail closed BEFORE any transaction is opened: an audited command that declares nothing can never be verified.
                if (expectations.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"{typeof(TRequest).Name} is an audited request but declares no [AuditExpectation]; it was not executed.");
                }
            }

            return await _unitOfWork.ExecuteAsync(async token =>
            {
                // Only entries staged AFTER this point can satisfy this command: anything staged earlier in the same request
                // (by other code, or by an enclosing command) is outside its window.
                var windowStart = _auditState.Staged.Count;

                var response = await next(token);

                if (expectations is not null)
                {
                    VerifyExpectations(expectations, windowStart);
                }

                return response;
            }, cancellationToken);
        }

        // Throwing here, before the commit, rolls back the business change too: a change that must be audited never commits
        // without a matching audit entry. Each declaration consumes one distinct staged entry.
        private void VerifyExpectations(IReadOnlyList<AuditExpectation> expectations, int windowStart)
        {
            var available = _auditState.Staged.Skip(windowStart).ToList();
            var missing = new List<AuditExpectation>();

            foreach (var expectation in expectations)
            {
                var index = available.FindIndex(s =>
                    string.Equals(s.Module, expectation.Module, StringComparison.Ordinal)
                    && string.Equals(s.Action, expectation.Action, StringComparison.Ordinal));

                if (index < 0)
                {
                    missing.Add(expectation);
                }
                else
                {
                    available.RemoveAt(index);
                }
            }

            if (missing.Count > 0)
            {
                var stagedNames = _auditState.Staged.Skip(windowStart).Select(s => $"{s.Module}/{s.Action}").ToList();

                throw new InvalidOperationException(
                    $"{typeof(TRequest).Name} is an audited request but its handler did not stage the expected audit entry: " +
                    $"{string.Join(", ", missing.Select(m => $"{m.Module}/{m.Action}"))}. " +
                    $"Staged during the command: {(stagedNames.Count == 0 ? "none" : string.Join(", ", stagedNames))}. The operation was rolled back.");
            }
        }
    }
}
