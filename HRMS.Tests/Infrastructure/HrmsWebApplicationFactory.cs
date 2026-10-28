using System.Net;
using HRMS.API;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HRMS.Tests.Infrastructure;

/// <summary>
/// Hosts the real API in-process against the disposable container database.
/// Uses the "Testing" environment, so neither seeder runs and appsettings.Development.json is never loaded.
/// Only the connection string and the JWT secret are overridden; everything else (issuer, audience, the 480-minute
/// lifetime) comes from the shipped appsettings.json.
/// </summary>
public sealed class HrmsWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestRemoteIp = "203.0.113.7";

    private readonly PostgresFixture _fixture;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly ScratchDatabase? _scratch;
    private readonly string _environmentName;
    private readonly IReadOnlyDictionary<string, string?> _extraSettings;

    /// <param name="scratch">
    /// Optional: host the API against a throwaway database inside the same disposable container instead of the shared one
    /// (used by tests that need an empty Companies table). The same guard applies to it.
    /// </param>
    public HrmsWebApplicationFactory(PostgresFixture fixture, Action<IServiceCollection>? configureServices = null, ScratchDatabase? scratch = null, IReadOnlyDictionary<string, string?>? settings = null, string environmentName = "Testing")
    {
        _environmentName = environmentName;
        _extraSettings = settings ?? new Dictionary<string, string?>();
        _fixture = fixture;
        _configureServices = configureServices;
        _scratch = scratch;
    }

    private string TargetConnectionString => _scratch?.ConnectionString ?? _fixture.ConnectionString;

    private void VerifyTargetString()
    {
        if (_scratch is null)
        {
            _fixture.VerifyTarget();
        }
        else
        {
            _fixture.VerifyTarget();                                                       // the container itself
            TestDatabaseGuard.VerifyConnectionString(_scratch.ConnectionString, _scratch.Name);  // and the scratch database
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        VerifyTargetString();

        builder.UseEnvironment(_environmentName);

        // Seeders and the Super Admin bootstrap read builder.Configuration in Program.cs, which only sees host settings, not ConfigureAppConfiguration.
        foreach (var pair in _extraSettings)
        {
            if (pair.Key.StartsWith("HRMS_", StringComparison.Ordinal)) builder.UseSetting(pair.Key, pair.Value);
        }

        // Mvc.Testing derives the content root with an MSBuild property function that breaks on folder names containing
        // an apostrophe (this repository lives under "Aditya's_Backup"), so locate HRMS.API explicitly.
        builder.UseContentRoot(FindApiProjectDirectory());

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            // Added last, so these win over appsettings.json and any environment variables.
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = TargetConnectionString,
                ["Jwt:Secret"] = _fixture.JwtSecret,

                // The polling worker would deliver messages in the background, which makes tests non-deterministic; the tests drive the
                // outbox processor explicitly (and the inline attempt after the commit stays on, as in production).
                ["Outbox:WorkerEnabled"] = "false"
            };

            // Many tests log in or reissue from the one fake address; the production limits are exercised by dedicated tests that
            // lower them on the host's RateLimitingOptions instance.
            foreach (var rule in new[]
            {
                "LoginPerIp", "LoginPerAccountAndIp", "SetPasswordPerIp", "SetPasswordPerToken", "ChangePasswordPerIp",
                "ChangePasswordPerUser", "InvitationReissuePerIp", "InvitationReissuePerActor", "InvitationReissuePerTarget"
            })
            {
                settings[$"RateLimiting:{rule}:Permits"] = "1000000";
            }

            foreach (var pair in _extraSettings)
            {
                settings[pair.Key] = pair.Value;
            }

            configuration.AddInMemoryCollection(settings);
        });

        builder.ConfigureServices(services =>
        {
            services.AddTransient<IStartupFilter, FakeRemoteIpStartupFilter>();
            _configureServices?.Invoke(services);
        });
    }

    private static string FindApiProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HRMS.slnx")))
            {
                var api = Path.Combine(directory.FullName, "HRMS.API");

                if (File.Exists(Path.Combine(api, "HRMS.API.csproj")))
                {
                    return api;
                }
            }
        }

        throw new InvalidOperationException("Could not locate the HRMS.API project directory from the test output folder.");
    }

    public string Issuer => Configuration["Jwt:Issuer"]!;

    public string Audience => Configuration["Jwt:Audience"]!;

    private IConfiguration Configuration => Services.GetRequiredService<IConfiguration>();

    /// <summary>The host must be using exactly the fixture's database; anything else aborts the test.</summary>
    public void AssertUsesFixtureDatabase()
    {
        var actual = Configuration["ConnectionStrings:DefaultConnection"];

        if (!string.Equals(actual, TargetConnectionString, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TEST DATABASE GUARD: the test host is not using the expected disposable database.");
        }

        VerifyTargetString();
    }

    private sealed class FakeRemoteIpStartupFilter : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(TestRemoteIp);
                    return nextMiddleware(context);
                });

                next(app);
            };
        }
    }
}
