namespace HRMS.Domain.Authorization
{
    /// <summary>How much data a user may reach for a resource. Platform-wide (Super Admin across companies) is deferred.</summary>
    public enum DataScope
    {
        None = 0,
        Own = 1,
        Team = 2,
        Company = 3
    }
}
