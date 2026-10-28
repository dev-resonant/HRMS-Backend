namespace HRMS.Application.Interfaces
{
    public interface IClientInfo
    {
        string? IpAddress { get; }

        string? UserAgent { get; }
    }
}
