namespace HRMS.Application.Common.Exceptions
{
    /// <summary>The request conflicts with the current state of the data (duplicate value, invalid state). Mapped to 409. The message must be safe to show to the caller.</summary>
    public class ConflictException : Exception
    {
        public ConflictException(string message) : base(message)
        {
        }
    }
}
