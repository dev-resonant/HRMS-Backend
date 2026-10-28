namespace HRMS.Domain.Common
{
    /// <summary>
    /// The single normalization rule for company domains (the host name a tenant is resolved from): trim surrounding
    /// whitespace, then lowercase with the invariant culture - the same initial policy as <see cref="EmailNormalizer"/>.
    /// Used by both seeders, the Company.Domain setter and the tenant resolver, so a domain is written, looked up and
    /// matched against the request host the same way everywhere.
    /// </summary>
    public static class DomainNormalizer
    {
        public static string Normalize(string domain)
        {
            ArgumentNullException.ThrowIfNull(domain);

            return domain.Trim().ToLowerInvariant();
        }
    }
}
