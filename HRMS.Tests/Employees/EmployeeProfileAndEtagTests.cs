using System.Net;
using System.Text.Json;
using HRMS.Domain.Enums;
using HRMS.Tests.Infrastructure;

namespace HRMS.Tests.Employees;

/// <summary>The optional onboarding-form fields (personal email, phone, employment type) and the ETag / If-Match contract.</summary>
[Collection(PostgresCollection.Name)]
public class EmployeeProfileAndEtagTests : EmployeeApiTestBase
{
    public EmployeeProfileAndEtagTests(PostgresFixture fx) : base(fx, settings: new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = "http://localhost:5173" }) { }

    private async Task<(AuthzCompany Company, AuthzUser Hr)> SetupAsync()
    {
        var company = await NewCompanyAsync();
        return (company, await HrAsync(company));
    }

    private static Dictionary<string, object?> CreateWith(Guid departmentId, params (string Key, object? Value)[] extra)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Asha Rao", ["email"] = $"asha-{Guid.NewGuid():N}@example.test", ["departmentId"] = departmentId,
            ["designation"] = "Engineer", ["joiningDate"] = "2024-03-01"
        };

        foreach (var (key, value) in extra) body[key] = value;
        return body;
    }

    private static Dictionary<string, object?> UpdateWith(Guid departmentId, params (string Key, object? Value)[] extra)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Tara Target", ["departmentId"] = departmentId, ["managerId"] = null, ["designation"] = "Engineer",
            ["joiningDate"] = "2024-01-15", ["status"] = "active", ["lastWorkingDate"] = null
        };

        foreach (var (key, value) in extra) body[key] = value;
        return body;
    }

    // ================= profile fields =================

    [Fact]
    public async Task The_optional_fields_are_stored_returned_listed_and_audited()
    {
        var (company, hr) = await SetupAsync();

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token,
            CreateWith(company.DepartmentId, ("personalEmail", "  Asha.Private@Example.TEST "), ("phone", " +91 98765 43210 "), ("employmentType", "Part_Time")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal("asha.private@example.test", body.GetProperty("personalEmail").GetString());
        Assert.Equal("+91 98765 43210", body.GetProperty("phone").GetString());
        Assert.Equal("part_time", body.GetProperty("employmentType").GetString());

        var id = body.GetProperty("id").GetGuid();
        var (_, employee) = await LoadAsync(id);
        Assert.Equal("asha.private@example.test", employee.PersonalEmail);
        Assert.Equal("+91 98765 43210", employee.Phone);
        Assert.Equal(EmploymentType.PartTime, employee.EmploymentType);

        var single = await JsonOf(await GetAsync(company, $"/api/employees/{id}", hr.Token));
        Assert.Equal("part_time", single.GetProperty("employmentType").GetString());
        var listed = (await JsonOf(await GetAsync(company, "/api/employees?search=Asha", hr.Token))).GetProperty("items")[0];
        Assert.Equal("+91 98765 43210", listed.GetProperty("phone").GetString());

        var audit = Assert.Single(await AuditAsync(company.CompanyId, "employee.created"));
        using var state = JsonDocument.Parse(audit.NewValue!);
        Assert.Equal("asha.private@example.test", state.RootElement.GetProperty("personalEmail").GetString());
        Assert.Equal("part_time", state.RootElement.GetProperty("employmentType").GetString());
    }

    [Fact]
    public async Task The_fields_are_optional_and_default_to_null()
    {
        var (company, hr) = await SetupAsync();

        var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateWith(company.DepartmentId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("personalEmail").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("phone").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("employmentType").ValueKind);
    }

    [Theory]
    [InlineData("personalEmail", "not-an-email")]
    [InlineData("personalEmail", "bad\u0000@example.test")]
    [InlineData("phone", "abc")]
    [InlineData("phone", "1234")]                                       // too short
    [InlineData("phone", "+1234567890123456789012")]                    // too long
    [InlineData("phone", "555-1234; DROP TABLE")]
    [InlineData("employmentType", "intern")]
    [InlineData("employmentType", "full-time")]                          // the API value is full_time
    public async Task Invalid_values_are_rejected_on_create_and_update_and_change_nothing(string field, string value)
    {
        var (company, hr) = await SetupAsync();
        var target = await SeedAsync(company, "Tara Target", "EMP-0010");
        var before = await CountsAsync(company.CompanyId);

        var created = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateWith(company.DepartmentId, (field, value)));
        var updated = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token, UpdateWith(company.DepartmentId, (field, value)));

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
        Assert.Equal(before, await CountsAsync(company.CompanyId));
    }

    [Fact]
    public async Task All_three_employment_types_round_trip()
    {
        var (company, hr) = await SetupAsync();

        foreach (var (name, expected) in new[] { ("full_time", EmploymentType.FullTime), ("PART_TIME", EmploymentType.PartTime), ("contract", EmploymentType.Contract) })
        {
            var response = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateWith(company.DepartmentId, ("employmentType", name)));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(expected, (await LoadAsync((await JsonOf(response)).GetProperty("id").GetGuid())).Employee.EmploymentType);
        }
    }

    [Fact]
    public async Task Update_replaces_the_fields_and_null_clears_them_with_a_before_and_after_audit()
    {
        var (company, hr) = await SetupAsync();
        var target = await SeedAsync(company, "Tara Target", "EMP-0010");

        var set = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token,
            UpdateWith(company.DepartmentId, ("personalEmail", "tara@home.test"), ("phone", "(022) 555 0100"), ("employmentType", "contract")));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(EmploymentType.Contract, (await LoadAsync(target.EmployeeId)).Employee.EmploymentType);

        var cleared = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token, UpdateWith(company.DepartmentId));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var (_, employee) = await LoadAsync(target.EmployeeId);
        Assert.Null(employee.PersonalEmail);
        Assert.Null(employee.Phone);
        Assert.Null(employee.EmploymentType);

        var last = (await AuditAsync(company.CompanyId, "employee.updated")).Last();
        using var before = JsonDocument.Parse(last.OldValue!);
        using var after = JsonDocument.Parse(last.NewValue!);
        Assert.Equal("tara@home.test", before.RootElement.GetProperty("personalEmail").GetString());
        Assert.Equal(JsonValueKind.Null, after.RootElement.GetProperty("personalEmail").ValueKind);
    }

    [Fact]
    public async Task The_fields_stay_inside_their_company()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var created = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateWith(company.DepartmentId, ("phone", "+91 98765 43210")));
        var id = (await JsonOf(created)).GetProperty("id").GetGuid();
        var otherHr = await HrAsync(other);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(other, $"/api/employees/{id}", otherHr.Token)).StatusCode);
        Assert.Empty((await JsonOf(await GetAsync(other, "/api/employees?search=98765", otherHr.Token))).GetProperty("items").EnumerateArray());
    }

    // ================= ETag / If-Match =================

    private static string ETagOf(HttpResponseMessage response) => response.Headers.ETag!.ToString();

    [Fact]
    public async Task Reads_and_writes_return_an_ETag_and_the_version_is_not_in_the_json()
    {
        var (company, hr) = await SetupAsync();
        var target = await SeedAsync(company, "Tara Target", "EMP-0010");

        var get = await GetAsync(company, $"/api/employees/{target.EmployeeId}", hr.Token);

        Assert.Matches("^\"[0-9]+\"$", ETagOf(get));                                       // strong, quoted, opaque
        Assert.DoesNotContain("version", (await get.Content.ReadAsStringAsync()), StringComparison.OrdinalIgnoreCase);

        var put = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token, UpdateWith(company.DepartmentId, ("designation", "Senior")));
        var etagAfter = ETagOf(put);
        Assert.NotEqual(ETagOf(get), etagAfter);                                           // a change moves the version
        Assert.Equal(etagAfter, ETagOf(await GetAsync(company, $"/api/employees/{target.EmployeeId}", hr.Token)));   // and GET agrees with PUT

        var created = await CallAsync(company, HttpMethod.Post, "/api/employees", hr.Token, CreateWith(company.DepartmentId));
        var createdId = (await JsonOf(created)).GetProperty("id").GetGuid();
        Assert.Equal(ETagOf(created), ETagOf(await GetAsync(company, $"/api/employees/{createdId}", hr.Token)));
    }

    [Fact]
    public async Task A_matching_If_Match_succeeds_and_a_stale_one_changes_nothing()
    {
        var (company, hr) = await SetupAsync();
        var target = await SeedAsync(company, "Tara Target", "EMP-0010");
        var etag = ETagOf(await GetAsync(company, $"/api/employees/{target.EmployeeId}", hr.Token));

        var first = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token,
            UpdateWith(company.DepartmentId, ("designation", "First")), ("If-Match", etag));
        var stale = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token,
            UpdateWith(company.DepartmentId, ("designation", "Stale")), ("If-Match", etag));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Contains("changed since you loaded", await stale.Content.ReadAsStringAsync());
        Assert.Equal("First", (await LoadAsync(target.EmployeeId)).Employee.Designation);
        Assert.Single(await AuditAsync(company.CompanyId, "employee.updated"));            // the refused attempt left no audit row

        var fresh = ETagOf(first);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token,
            UpdateWith(company.DepartmentId, ("designation", "Second")), ("If-Match", fresh))).StatusCode);
    }

    [Fact]
    public async Task A_stale_form_is_refused_even_when_the_other_change_was_a_name_only_edit()
    {
        var (company, hr) = await SetupAsync();
        var second = await HrAsync(company);
        var target = await SeedAsync(company, "Tara Target", "EMP-0010");
        var etag = ETagOf(await GetAsync(company, $"/api/employees/{target.EmployeeId}", hr.Token));

        // Administrator B changes only the name (stored on the account row, not the employee row).
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", second.Token,
            UpdateWith(company.DepartmentId, ("name", "Tara Renamed")))).StatusCode);

        var stale = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token,
            UpdateWith(company.DepartmentId, ("name", "Tara Overwritten")), ("If-Match", etag));

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("Tara Renamed", (await LoadAsync(target.EmployeeId)).User.Name);
    }

    [Fact]
    public async Task Without_If_Match_the_behavior_is_unchanged_and_star_means_any_current_version()
    {
        var (company, hr) = await SetupAsync();
        var target = await SeedAsync(company, "Tara Target", "EMP-0010");

        var none = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token, UpdateWith(company.DepartmentId, ("designation", "A")));
        var star = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token, UpdateWith(company.DepartmentId, ("designation", "B")), ("If-Match", "*"));

        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Equal(HttpStatusCode.OK, star.StatusCode);
        Assert.Equal("B", (await LoadAsync(target.EmployeeId)).Employee.Designation);
    }

    [Theory]
    [InlineData("\"1\"")]                       // a version that is not current
    [InlineData("\"not-a-number\"")]
    [InlineData("W/\"1\"")]                      // weak tags never match If-Match
    public async Task A_version_that_is_not_current_gives_412(string header)
    {
        var (company, hr) = await SetupAsync();
        var target = await SeedAsync(company, "Tara Target", "EMP-0010");

        var response = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token, UpdateWith(company.DepartmentId, ("designation", "X")), ("If-Match", header));

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("Engineer", (await LoadAsync(target.EmployeeId)).Employee.Designation);
    }

    [Fact]
    public async Task A_list_of_tags_matches_if_any_is_current_and_a_malformed_header_is_a_400()
    {
        var (company, hr) = await SetupAsync();
        var target = await SeedAsync(company, "Tara Target", "EMP-0010");
        var etag = ETagOf(await GetAsync(company, $"/api/employees/{target.EmployeeId}", hr.Token));

        var list = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token,
            UpdateWith(company.DepartmentId, ("designation", "ListMatch")), ("If-Match", $"\"7\", {etag}, \"9\""));
        var malformed = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token,
            UpdateWith(company.DepartmentId, ("designation", "Malformed")), ("If-Match", "this is not an entity tag"));

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("ListMatch", (await LoadAsync(target.EmployeeId)).Employee.Designation);
    }

    [Fact]
    public async Task Deactivation_honors_If_Match_too()
    {
        var (company, hr) = await SetupAsync();
        var target = await SeedAsync(company, "Tara Target", "EMP-0010");
        var etag = ETagOf(await GetAsync(company, $"/api/employees/{target.EmployeeId}", hr.Token));
        await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}", hr.Token, UpdateWith(company.DepartmentId, ("designation", "Moved on")));

        var stale = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}/deactivate", hr.Token, new { lastWorkingDate = "2025-12-31" }, ("If-Match", etag));

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var (user, employee) = await LoadAsync(target.EmployeeId);
        Assert.True(user.IsActive);                                                         // nothing happened
        Assert.Equal(EmployeeStatus.Active, employee.Status);
        Assert.Single(await TestData.GetSessionsAsync(Fx, target.UserId));
        Assert.Empty(await AuditAsync(company.CompanyId, "employee.deactivated"));

        var current = ETagOf(await GetAsync(company, $"/api/employees/{target.EmployeeId}", hr.Token));
        var ok = await CallAsync(company, HttpMethod.Put, $"/api/employees/{target.EmployeeId}/deactivate", hr.Token, new { lastWorkingDate = "2025-12-31" }, ("If-Match", current));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.NotEqual(current, ETagOf(ok));
    }

    [Fact]
    public async Task A_foreign_or_missing_employee_is_still_a_404_whatever_the_If_Match_says()
    {
        var (company, hr) = await SetupAsync();
        var other = await NewCompanyAsync();
        var foreign = await SeedAsync(other, "Fiona Foreign", "EMP-0001");

        var viaForeign = await CallAsync(company, HttpMethod.Put, $"/api/employees/{foreign.EmployeeId}", hr.Token, UpdateWith(company.DepartmentId), ("If-Match", "\"1\""));
        var missing = await CallAsync(company, HttpMethod.Put, $"/api/employees/{Guid.NewGuid()}", hr.Token, UpdateWith(company.DepartmentId), ("If-Match", "\"1\""));

        Assert.Equal(HttpStatusCode.NotFound, viaForeign.StatusCode);                       // no version information leaks across tenants
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await viaForeign.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Browsers_are_allowed_to_read_the_ETag_header()
    {
        var (company, hr) = await SetupAsync();
        var target = await SeedAsync(company, "Tara Target", "EMP-0010");
        var client = Factory.ClientFor(company.Domain);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/employees/{target.EmployeeId}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", hr.Token);
        request.Headers.Add("Origin", "http://localhost:5173");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ETag", string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers")), StringComparison.OrdinalIgnoreCase);
    }
}
