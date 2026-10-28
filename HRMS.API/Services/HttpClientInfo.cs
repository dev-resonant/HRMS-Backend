using System.Net;
using System.Net.Sockets;
using HRMS.Application.Interfaces;

namespace HRMS.API.Services
{
    public class HttpClientInfo : IClientInfo
    {
        private const int MaxUserAgentLength = 512;

        // Matches the VARCHAR(45) ip_address columns (longest textual IPv6 form).
        private const int MaxIpAddressLength = 45;

        private readonly IHttpContextAccessor _httpContextAccessor;

        public HttpClientInfo(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public string? IpAddress => Normalize(_httpContextAccessor.HttpContext?.Connection.RemoteIpAddress);

        public string? UserAgent
        {
            get
            {
                var value = _httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString();

                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                return value.Length > MaxUserAgentLength ? value[..MaxUserAgentLength] : value;
            }
        }

        // Unwraps IPv4-mapped IPv6 addresses (::ffff:1.2.3.4 -> 1.2.3.4) and drops the IPv6 scope id
        // (fe80::1%12 -> fe80::1) so the value always fits the column without truncating a real address.
        private static string? Normalize(IPAddress? address)
        {
            if (address is null)
            {
                return null;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            {
                address = new IPAddress(address.GetAddressBytes());
            }

            var text = address.ToString();

            return text.Length <= MaxIpAddressLength ? text : null;
        }
    }
}
