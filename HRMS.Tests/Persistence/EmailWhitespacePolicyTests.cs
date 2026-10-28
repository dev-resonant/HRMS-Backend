using HRMS.Domain.Common;
using HRMS.Infrastructure.Persistence;
using HRMS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit.Abstractions;

namespace HRMS.Tests.Persistence;

/// <summary>
/// Exhaustive proof that the APPLIED database constraint (created by the real EF migration) and the application's
/// EmailNormalizer.Trim() agree. In a throwaway database inside the disposable container, every character of the Basic
/// Multilingual Plane is tried at the start and at the end of an email address by inserting a user row inside a
/// subtransaction that is always rolled back. Nothing outside that scratch database is touched.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EmailWhitespacePolicyTests
{
    private readonly PostgresFixture _fx;
    private readonly ITestOutputHelper _output;

    public EmailWhitespacePolicyTests(PostgresFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _output = output;
    }

    private const string ProbeScript = @"
create temp table probe_result(kind text not null, cp int not null);
create temp table probe_case(cp int not null);
do $$
declare
    i int;
    cid uuid := gen_random_uuid();
    rid uuid := gen_random_uuid();
begin
    insert into ""Companies""(""Id"",""Name"",""Domain"",""LogoUrl"",""Timezone"",""IsActive"",""CreatedAt"")
        values (cid,'Probe','probe.test','','UTC',true,now());
    insert into ""Roles""(""Id"",""CompanyId"",""Name"",""DisplayName"",""Permissions"",""IsSystem"",""CreatedAt"")
        values (rid,cid,'Admin','Administrator','{}',true,now());

    for i in 1..65535 loop
        continue when i between 55296 and 57343;                    -- surrogate halves are not characters on their own
        if lower(chr(i)) <> chr(i) then insert into probe_case values (i); end if;

        begin
            insert into ""Users""(""Id"",""CompanyId"",""RoleId"",""Name"",""Email"",""PasswordHash"",""IsActive"",""IsFirstLogin"",""CreatedAt"")
                values (gen_random_uuid(), cid, rid, 'p', chr(i) || 'a@b.test', 'x', true, false, now());
            raise exception 'accepted' using errcode = 'P0001';       -- always roll the probe row back
        exception
            when check_violation then insert into probe_result values ('leading', i);
            when raise_exception then null;                           -- the insert was accepted (then rolled back)
        end;

        begin
            insert into ""Users""(""Id"",""CompanyId"",""RoleId"",""Name"",""Email"",""PasswordHash"",""IsActive"",""IsFirstLogin"",""CreatedAt"")
                values (gen_random_uuid(), cid, rid, 'p', 'a@b.test' || chr(i), 'x', true, false, now());
            raise exception 'accepted' using errcode = 'P0001';
        exception
            when check_violation then insert into probe_result values ('trailing', i);
            when raise_exception then null;
        end;
    end loop;
end $$;";

    private static async Task<HashSet<int>> ReadSetAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var set = new HashSet<int>();
        while (await reader.ReadAsync()) set.Add(reader.GetInt32(0));
        return set;
    }

    [Fact]
    public async Task The_applied_constraint_rejects_exactly_the_characters_the_application_trims_at_both_ends()
    {
        await using var database = await _fx.CreateScratchDatabaseAsync();
        await database.VerifyTargetAsync();
        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync();                  // all migrations, through the EF runner
        }

        await database.VerifyTargetAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var script = new NpgsqlCommand(ProbeScript, connection) { CommandTimeout = 300 })
        {
            await script.ExecuteNonQueryAsync();
        }

        var leading = await ReadSetAsync(connection, "select cp from probe_result where kind = 'leading'");
        var trailing = await ReadSetAsync(connection, "select cp from probe_result where kind = 'trailing'");
        var databaseUppercase = await ReadSetAsync(connection, "select cp from probe_case");

        var applicationWhitespace = Enumerable.Range(1, 0xFFFF)
            .Where(c => c is < 0xD800 or > 0xDFFF)
            .Where(c => char.IsWhiteSpace((char)c))
            .ToHashSet();
        var applicationUppercase = Enumerable.Range(1, 0xFFFF)
            .Where(c => c is < 0xD800 or > 0xDFFF)
            .Where(c => ((char)c).ToString().ToLowerInvariant() != ((char)c).ToString())
            .ToHashSet();

        // 1) the policy list is the .NET whitespace set, and the database rejects exactly that set at either end
        Assert.Equal(25, applicationWhitespace.Count);
        Assert.Equal(applicationWhitespace.OrderBy(c => c), UserEmailPolicy.TrimmedWhitespaceCodePoints.OrderBy(c => c));
        Assert.Equal(applicationWhitespace.OrderBy(c => c), leading.Except(databaseUppercase).OrderBy(c => c));
        Assert.Equal(applicationWhitespace.OrderBy(c => c), trailing.Except(databaseUppercase).OrderBy(c => c));

        // 2) apart from whitespace the database rejects only what its own lower() changes (i.e. uppercase letters)
        Assert.Equal(applicationWhitespace.Union(databaseUppercase).OrderBy(c => c), leading.OrderBy(c => c));
        Assert.Equal(applicationWhitespace.Union(databaseUppercase).OrderBy(c => c), trailing.OrderBy(c => c));

        // 3) the application and this PostgreSQL build must agree on ASCII case; other differences are REPORTED, not asserted
        //    (they depend on the database's locale/provider and Unicode version - see PROJECT_STATUS.md)
        Assert.Equal(applicationUppercase.Where(c => c <= 0x7F).OrderBy(c => c), databaseUppercase.Where(c => c <= 0x7F).OrderBy(c => c));

        var onlyDotNet = applicationUppercase.Except(databaseUppercase).OrderBy(c => c).ToList();
        var onlyDatabase = databaseUppercase.Except(applicationUppercase).OrderBy(c => c).ToList();
        _output.WriteLine($"Whitespace rejected at either end by the database: {leading.Intersect(applicationWhitespace).Count()} of {applicationWhitespace.Count}.");
        _output.WriteLine($"Characters .NET lowercases but this PostgreSQL does not (count {onlyDotNet.Count}): {string.Join(" ", onlyDotNet.Take(20).Select(c => $"U+{c:X4}"))}{(onlyDotNet.Count > 20 ? " ..." : "")}");
        _output.WriteLine($"Characters this PostgreSQL lowercases but .NET does not (count {onlyDatabase.Count}): {string.Join(" ", onlyDatabase.Take(20).Select(c => $"U+{c:X4}"))}{(onlyDatabase.Count > 20 ? " ..." : "")}");
    }
}
