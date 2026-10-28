namespace HRMS.API.RateLimiting
{
    /// <summary>At most <see cref="Permits"/> requests per fixed window of <see cref="WindowSeconds"/> seconds.</summary>
    public sealed class RateLimitRule
    {
        public RateLimitRule()
        {
        }

        public RateLimitRule(int permits, int windowSeconds)
        {
            Permits = permits;
            WindowSeconds = windowSeconds;
        }

        public int Permits { get; set; }

        public int WindowSeconds { get; set; }

        public TimeSpan Window => TimeSpan.FromSeconds(Math.Max(1, WindowSeconds));
    }

    /// <summary>
    /// Limits of the credential endpoints (appsettings section "RateLimiting"). Counters are kept in memory per application instance and
    /// reset when it restarts; a window ends by itself, so nobody is ever locked out permanently. No limit applies to an account because
    /// of what OTHER clients do: per-account counters are always combined with the caller's own address (login) or need a secret the
    /// attacker does not have (set-password token), and per-user counters (change-password, re-issue) need a valid session.
    /// </summary>
    public sealed class RateLimitingOptions
    {
        public const string SectionName = "RateLimiting";

        public bool Enabled { get; set; } = true;

        /// <summary>All login attempts from one address, whichever account they name.</summary>
        public RateLimitRule LoginPerIp { get; set; } = new(20, 60);

        /// <summary>Attempts from one address at one (tenant, e-mail) pair - identical for existing and non-existing accounts.</summary>
        public RateLimitRule LoginPerAccountAndIp { get; set; } = new(10, 300);

        public RateLimitRule SetPasswordPerIp { get; set; } = new(10, 60);

        /// <summary>Submissions of one particular token (keyed by its hash), whoever sends them.</summary>
        public RateLimitRule SetPasswordPerToken { get; set; } = new(10, 900);

        public RateLimitRule ChangePasswordPerIp { get; set; } = new(30, 60);

        public RateLimitRule ChangePasswordPerUser { get; set; } = new(10, 900);

        public RateLimitRule InvitationReissuePerIp { get; set; } = new(60, 60);

        /// <summary>Re-issues by one authenticated administrator.</summary>
        public RateLimitRule InvitationReissuePerActor { get; set; } = new(30, 3600);

        /// <summary>Re-issues for one employee (stops a mailbox from being flooded, whoever asks).</summary>
        public RateLimitRule InvitationReissuePerTarget { get; set; } = new(5, 3600);
    }

    public enum AuthRateLimitPolicy
    {
        Login,
        SetPassword,
        ChangePassword,
        InvitationReissue
    }

    /// <summary>Marks an action as rate limited by the policy; the global limiter reads it from the endpoint metadata.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class AuthRateLimitAttribute : Attribute
    {
        public AuthRateLimitAttribute(AuthRateLimitPolicy policy) => Policy = policy;

        public AuthRateLimitPolicy Policy { get; }
    }
}
