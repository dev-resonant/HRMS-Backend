using Npgsql;

namespace HRMS.Tests.Infrastructure;

/// <summary>
/// Refuses to let a test touch any database that is not the disposable container created by the current fixture.
/// There is deliberately no fallback: if a check fails the exception stops the test run.
/// </summary>
public static class TestDatabaseGuard
{
    private const string DatabasePrefix = "hrms_test_";
    private const string MarkerTable = "hrms_test_marker";

    // Ports used by the developer's existing PostgreSQL servers (native Windows = 5432, docker-compose = 5433).
    private static readonly int[] ForbiddenPorts = { 5432, 5433 };

    /// <summary>Static check of the connection string. Never opens a connection.</summary>
    public static void VerifyConnectionString(string connectionString, string expectedDatabase)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("TEST DATABASE GUARD: no connection string.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        if (builder.Host is not ("localhost" or "127.0.0.1" or "::1"))
        {
            throw new InvalidOperationException("TEST DATABASE GUARD: host is not local.");
        }

        if (Array.IndexOf(ForbiddenPorts, builder.Port) >= 0)
        {
            throw new InvalidOperationException($"TEST DATABASE GUARD: port {builder.Port} belongs to an existing PostgreSQL server.");
        }

        if (string.IsNullOrEmpty(builder.Database) ||
            !builder.Database.StartsWith(DatabasePrefix, StringComparison.Ordinal) ||
            !string.Equals(builder.Database, expectedDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TEST DATABASE GUARD: database is not the one created by this test run.");
        }
    }

    /// <summary>
    /// Server-side proof that the database is the one created by this fixture: it must contain a marker row holding
    /// this run's unique id. An existing database can never contain it.
    /// </summary>
    public static async Task VerifyMarkerAsync(string connectionString, string runId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"select run_id from {MarkerTable} limit 1";

        var value = await command.ExecuteScalarAsync() as string;

        if (!string.Equals(value, runId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TEST DATABASE GUARD: marker does not match this test run.");
        }
    }

    /// <summary>Creates the marker. Refuses unless the database is completely empty (i.e. brand new).</summary>
    public static async Task CreateMarkerAsync(string connectionString, string runId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "select count(*) from information_schema.tables where table_schema not in ('pg_catalog','information_schema')";
            var existing = Convert.ToInt64(await check.ExecuteScalarAsync());

            if (existing != 0)
            {
                throw new InvalidOperationException("TEST DATABASE GUARD: refusing to use a database that already contains tables.");
            }
        }

        await using var create = connection.CreateCommand();
        create.CommandText = $"create table {MarkerTable}(run_id text not null); insert into {MarkerTable}(run_id) values (@run)";
        create.Parameters.AddWithValue("run", runId);
        await create.ExecuteNonQueryAsync();
    }
}
