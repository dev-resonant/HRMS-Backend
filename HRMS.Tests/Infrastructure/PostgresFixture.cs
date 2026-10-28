using System.Security.Cryptography;
using HRMS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace HRMS.Tests.Infrastructure;

/// <summary>
/// Starts ONE disposable PostgreSQL 16 container for the whole test run (random name, random host port, no volume),
/// applies all migrations to its empty database and removes it afterwards. Never connects to any existing database.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string RunId { get; } = Guid.NewGuid().ToString("N");

    public string DatabaseName => $"hrms_test_{RunId[..12]}";

    /// <summary>Random per-run signing key for the test host; unrelated to any real secret.</summary>
    public string JwtSecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        try
        {
            _container = new PostgreSqlBuilder("postgres:16")
                .WithDatabase(DatabaseName)
                .WithUsername("hrms_test")
                .WithPassword(password)
                .Build();

            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Docker is required for the HRMS integration tests and could not start the disposable PostgreSQL container. " +
                "The tests never fall back to an existing database.", ex);
        }

        ConnectionString = _container.GetConnectionString();

        // 1) static checks, 2) the database must be brand new, 3) stamp it with this run's marker, 4) verify the marker.
        TestDatabaseGuard.VerifyConnectionString(ConnectionString, DatabaseName);
        await TestDatabaseGuard.CreateMarkerAsync(ConnectionString, RunId);
        await VerifyTargetAsync();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Synchronous string check; runs before every host or DbContext is created.</summary>
    public void VerifyTarget() => TestDatabaseGuard.VerifyConnectionString(ConnectionString, DatabaseName);

    /// <summary>Full check including the server-side marker; runs before every database-changing test operation.</summary>
    public async Task VerifyTargetAsync()
    {
        VerifyTarget();
        await TestDatabaseGuard.VerifyMarkerAsync(ConnectionString, RunId);
    }

    public HrmsDbContext CreateDbContext()
    {
        VerifyTarget();

        var options = new DbContextOptionsBuilder<HrmsDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new HrmsDbContext(options);
    }

    /// <summary>
    /// Creates an extra, empty database INSIDE this run's disposable container (used to test a migration against data that
    /// already exists). It gets its own guard marker and is dropped afterwards; the container is removed at the end of the run.
    /// </summary>
    public async Task<ScratchDatabase> CreateScratchDatabaseAsync()
    {
        await VerifyTargetAsync();

        var name = "hrms_test_" + Guid.NewGuid().ToString("N")[..12];

        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"create database \"{name}\"";     // name is generated here: hex characters only
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name, Pooling = false }.ConnectionString;
        var marker = Guid.NewGuid().ToString("N");

        TestDatabaseGuard.VerifyConnectionString(connectionString, name);
        await TestDatabaseGuard.CreateMarkerAsync(connectionString, marker);
        await TestDatabaseGuard.VerifyMarkerAsync(connectionString, marker);

        return new ScratchDatabase(this, name, connectionString, marker);
    }

    internal async Task DropScratchDatabaseAsync(string name)
    {
        await VerifyTargetAsync();

        await using var admin = new NpgsqlConnection(ConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"drop database if exists \"{name}\" with (force)";
        await drop.ExecuteNonQueryAsync();
    }
}

public sealed class ScratchDatabase : IAsyncDisposable
{
    private readonly PostgresFixture _owner;
    private readonly string _marker;

    internal ScratchDatabase(PostgresFixture owner, string name, string connectionString, string marker)
    {
        _owner = owner;
        Name = name;
        ConnectionString = connectionString;
        _marker = marker;
    }

    public string Name { get; }

    public string ConnectionString { get; }

    /// <summary>Every use re-runs the static guard and the marker check against this scratch database.</summary>
    public async Task VerifyTargetAsync()
    {
        TestDatabaseGuard.VerifyConnectionString(ConnectionString, Name);
        await TestDatabaseGuard.VerifyMarkerAsync(ConnectionString, _marker);
    }

    public HrmsDbContext CreateDbContext()
    {
        TestDatabaseGuard.VerifyConnectionString(ConnectionString, Name);

        var options = new DbContextOptionsBuilder<HrmsDbContext>().UseNpgsql(ConnectionString).Options;
        return new HrmsDbContext(options);
    }

    /// <summary>Applies every migration (through the EF migration runner) to this scratch database.</summary>
    public async Task MigrateToLatestAsync()
    {
        await VerifyTargetAsync();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task ExecuteAsync(string sql)
    {
        await VerifyTargetAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Same as <see cref="ExecuteAsync(string)"/> but with bind parameters (used for characters that are awkward in SQL text).</summary>
    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await VerifyTargetAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<string>> QueryAsync(string sql)
    {
        await VerifyTargetAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetValue(0)?.ToString() ?? "<null>");
        }

        return rows;
    }

    public async ValueTask DisposeAsync() => await _owner.DropScratchDatabaseAsync(Name);
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
