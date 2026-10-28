using HRMS.Tests.Infrastructure;

namespace HRMS.Tests.Persistence;

/// <summary>Proves the isolation guard refuses every existing database and accepts only this run's container.</summary>
[Collection(PostgresCollection.Name)]
public class TestDatabaseGuardTests
{
    private readonly PostgresFixture _fx;

    public TestDatabaseGuardTests(PostgresFixture fx) => _fx = fx;

    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=hrms_test_abc;Username=u;Password=p")]                       // native Windows PostgreSQL
    [InlineData("Host=localhost;Port=5433;Database=hrms_test_abc;Username=u;Password=p")]                       // docker-compose PostgreSQL
    [InlineData("Host=ep-example.neon.tech;Port=55123;Database=hrms_test_abc;Username=u;Password=p")]           // remote host (e.g. Neon)
    [InlineData("Host=10.0.0.5;Port=55123;Database=hrms_test_abc;Username=u;Password=p")]                       // any non-local host
    [InlineData("Host=localhost;Port=55123;Database=hrms_db;Username=u;Password=p")]                            // existing database name
    [InlineData("Host=localhost;Port=55123;Database=hrms_migration_test;Username=u;Password=p")]                // existing database name
    [InlineData("Host=localhost;Port=55123;Database=hrms_test_other;Username=u;Password=p")]                    // test-looking name, but not this run's
    [InlineData("")]
    public void The_guard_rejects_every_target_that_is_not_this_runs_container(string connectionString)
    {
        Assert.Throws<InvalidOperationException>(() =>
            TestDatabaseGuard.VerifyConnectionString(connectionString, "hrms_test_abc123456789"));
    }

    [Fact]
    public void The_guard_accepts_the_exact_target_it_was_given()
    {
        TestDatabaseGuard.VerifyConnectionString(
            "Host=localhost;Port=55123;Database=hrms_test_abc123456789;Username=u;Password=p", "hrms_test_abc123456789");
    }

    [Fact]
    public void The_fixtures_own_connection_passes_and_does_not_use_an_existing_servers_port()
    {
        _fx.VerifyTarget();

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(_fx.ConnectionString);
        Assert.NotEqual(5432, builder.Port);
        Assert.NotEqual(5433, builder.Port);
        Assert.StartsWith("hrms_test_", builder.Database);
    }

    [Fact]
    public async Task A_marker_from_another_run_is_rejected()
    {
        await _fx.VerifyTargetAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestDatabaseGuard.VerifyMarkerAsync(_fx.ConnectionString, Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task The_marker_cannot_be_created_in_a_database_that_already_contains_tables()
    {
        // The fixture database is already migrated, so it is not "brand new": the guard must refuse (and changes nothing).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestDatabaseGuard.CreateMarkerAsync(_fx.ConnectionString, Guid.NewGuid().ToString("N")));

        await _fx.VerifyTargetAsync();            // original marker still intact
    }
}
