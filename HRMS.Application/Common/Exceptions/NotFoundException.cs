namespace HRMS.Application.Common.Exceptions
{
    /// <summary>The requested record does not exist or is not visible to the caller (the two are deliberately indistinguishable). Mapped to 404.</summary>
    public class NotFoundException : Exception
    {
        public NotFoundException(string message) : base(message)
        {
        }
    }
}
