using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using HRMS.Application.Common.Exceptions;

namespace HRMS.API.RateLimiting
{
    /// <summary>
    /// Limits keyed by something that is only known after the request body has been read (the e-mail of a login, the token of a
    /// set-password call), so the ASP.NET Core middleware cannot see it. Same mechanism (fixed-window limiters from
    /// System.Threading.RateLimiting), same 429 answer. Keys are hashed before they are used as partition names, so no address or
    /// token is kept in memory in readable form.
    /// </summary>
    public sealed class AttemptLimiter
    {
        private readonly RateLimitingOptions _settings;
        private readonly PartitionedRateLimiter<string> _loginAccount;
        private readonly PartitionedRateLimiter<string> _setPasswordToken;

        public AttemptLimiter(RateLimitingOptions settings)
        {
            _settings = settings;
            _loginAccount = PartitionedRateLimiter.Create<string, string>(key => RateLimitingSetup.Window(key, settings.LoginPerAccountAndIp));
            _setPasswordToken = PartitionedRateLimiter.Create<string, string>(key => RateLimitingSetup.Window(key, settings.SetPasswordPerToken));
        }

        /// <summary>
        /// One login attempt of this client for this tenant and e-mail. The same budget applies whether or not the account exists, so the
        /// answer reveals nothing about it, and another client's attempts never consume it (the client's own address is part of the key).
        /// </summary>
        public void EnforceLogin(Guid companyId, string? clientAddressKey, string? email)
            => Enforce(_loginAccount, $"{companyId:N}|{clientAddressKey}|{email}");

        /// <summary>One submission of this setup token (whoever sends it).</summary>
        public void EnforceSetPassword(string? token)
            => Enforce(_setPasswordToken, token ?? string.Empty);

        private void Enforce(PartitionedRateLimiter<string> limiter, string rawKey)
        {
            if (!_settings.Enabled)
            {
                return;
            }

            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

            using var lease = limiter.AttemptAcquire(key);

            if (!lease.IsAcquired)
            {
                var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : TimeSpan.FromSeconds(1);

                throw new TooManyRequestsException(retryAfter);
            }
        }
    }
}
