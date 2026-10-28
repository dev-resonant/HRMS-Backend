namespace HRMS.Application.Common.Exceptions
{
    /// <summary>The caller's If-Match precondition no longer holds (the resource changed since it was read). Mapped to 412.</summary>
    public class PreconditionFailedException : Exception
    {
        public PreconditionFailedException(string message) : base(message)
        {
        }
    }
}
