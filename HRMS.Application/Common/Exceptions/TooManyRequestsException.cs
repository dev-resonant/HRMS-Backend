namespace HRMS.Application.Common.Exceptions
{
    /// <summary>Too many attempts in a short time. Mapped to 429 with a Retry-After header; the response is identical for every cause.</summary>
    public class TooManyRequestsException : Exception
    {
        public const string StandardMessage = "Too many requests. Please try again later.";

        public TooManyRequestsException(TimeSpan retryAfter) : base(StandardMessage)
        {
            RetryAfter = retryAfter;
        }

        public TimeSpan RetryAfter { get; }
    }
}
