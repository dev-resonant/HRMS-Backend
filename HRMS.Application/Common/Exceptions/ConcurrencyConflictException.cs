namespace HRMS.Application.Common.Exceptions
{
    /// <summary>
    /// The record was changed by someone else after it was read (optimistic concurrency), so nothing was written. A <see cref="ConflictException"/>
    /// (409) that a handler can tell apart from any other conflict or failure. The message is safe to show to the caller.
    /// </summary>
    public sealed class ConcurrencyConflictException : ConflictException
    {
        public ConcurrencyConflictException(string message) : base(message)
        {
        }
    }
}
