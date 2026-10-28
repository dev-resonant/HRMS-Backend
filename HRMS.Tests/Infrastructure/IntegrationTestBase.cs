namespace HRMS.Tests.Infrastructure;

/// <summary>
/// Base for tests that call the real API over HTTP. Each test instance gets its own host, all against the one
/// disposable container database owned by <see cref="PostgresFixture"/>.
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    protected IntegrationTestBase(PostgresFixture fixture)
    {
        Fx = fixture;
        Factory = new HrmsWebApplicationFactory(fixture);
        Factory.AssertUsesFixtureDatabase();
    }

    protected PostgresFixture Fx { get; }

    protected HrmsWebApplicationFactory Factory { get; }

    protected HttpClient ClientFor(string domain) => Factory.ClientFor(domain);

    public void Dispose() => Factory.Dispose();
}
