using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using HRMS.API.Authorization;
using HRMS.API.Controllers;
using HRMS.Application.Common.Transactions;
using HRMS.Application.Features.Employees;
using HRMS.Application.Features.Employees.Commands.CreateEmployee;
using HRMS.Application.Features.Employees.Commands.DeactivateEmployee;
using HRMS.Application.Features.Employees.Commands.UpdateEmployee;
using HRMS.Application.Features.Employees.Queries.GetEmployee;
using HRMS.Application.Features.Employees.Queries.ListEmployees;
using HRMS.Domain.Authorization;
using HRMS.Tests.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace HRMS.Tests.Employees;

/// <summary>Architecture and public-contract rules for the employee module.</summary>
public class EmployeeArchitectureTests
{
    [Theory]
    [InlineData(typeof(CreateEmployeeCommand), "employee", "employee.created")]
    [InlineData(typeof(UpdateEmployeeCommand), "employee", "employee.updated")]
    [InlineData(typeof(DeactivateEmployeeCommand), "employee", "employee.deactivated")]
    public void Every_data_changing_employee_command_is_audited_and_declares_exactly_its_audit_entry(Type command, string module, string action)
    {
        Assert.True(typeof(IAuditedRequest).IsAssignableFrom(command));
        Assert.True(typeof(ITransactionalRequest).IsAssignableFrom(command));
        Assert.Equal(new[] { new AuditExpectation(module, action) }, AuditExpectationAttribute.For(command));
    }

    [Theory]
    [InlineData(typeof(GetEmployeeQuery))]
    [InlineData(typeof(ListEmployeesQuery))]
    public void Employee_queries_are_read_only_and_not_wrapped_in_a_transaction(Type query)
    {
        Assert.False(typeof(ITransactionalRequest).IsAssignableFrom(query));
        Assert.Empty(AuditExpectationAttribute.For(query));
    }

    [Fact]
    public void The_employee_commands_carry_no_company_actor_role_or_credential_members()
    {
        var forbidden = new[] { "company", "actor", "user", "role", "password", "hash", "token", "empcode", "created" };

        foreach (var type in new[] { typeof(CreateEmployeeCommand), typeof(UpdateEmployeeCommand), typeof(DeactivateEmployeeCommand),
                                     typeof(CreateEmployeeRequest), typeof(UpdateEmployeeRequest), typeof(DeactivateEmployeeRequest) })
        {
            foreach (var property in type.GetProperties().Where(p => p.DeclaringType == type))
            {
                Assert.DoesNotContain(forbidden, f => property.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void Each_endpoint_requires_the_documented_permission()
    {
        var expected = new Dictionary<string, (string Module, string Action)>
        {
            [nameof(EmployeesController.List)] = (PermissionNames.Employees.Module, PermissionNames.Employees.ViewTeam),
            [nameof(EmployeesController.Get)] = (PermissionNames.Employees.Module, PermissionNames.Employees.ViewOwn),
            [nameof(EmployeesController.Create)] = (PermissionNames.Employees.Module, PermissionNames.Employees.Create),
            [nameof(EmployeesController.Update)] = (PermissionNames.Employees.Module, PermissionNames.Employees.EditAny),
            [nameof(EmployeesController.Deactivate)] = (PermissionNames.Employees.Module, PermissionNames.Employees.Deactivate),
            [nameof(EmployeesController.IssueInvitation)] = (PermissionNames.Employees.Module, PermissionNames.Employees.Create)
        };

        var actions = typeof(EmployeesController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Equal(expected.Keys.OrderBy(k => k), actions.Select(a => a.Name).OrderBy(n => n));

        foreach (var action in actions)
        {
            var required = Assert.Single(action.GetCustomAttributes<RequirePermissionAttribute>());
            Assert.Equal(expected[action.Name], (required.Module, required.Action));
            Assert.True(PermissionCatalog.IsValid(required.Module, required.Action));
            Assert.Empty(action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true));
        }
    }

    [Fact]
    public void No_EF_entity_is_part_of_the_controllers_public_signatures_or_the_dtos()
    {
        var entities = typeof(HRMS.Domain.Entities.Employee).Assembly.GetTypes().Where(t => t.Namespace == "HRMS.Domain.Entities").ToHashSet();
        var visited = new HashSet<Type>();

        void Inspect(Type type)
        {
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments()) Inspect(argument);
                type = type.GetGenericTypeDefinition();
            }

            if (!visited.Add(type)) return;

            Assert.DoesNotContain(type, entities);

            if (type.Namespace?.StartsWith("HRMS.Application.Features.Employees", StringComparison.Ordinal) == true)
            {
                foreach (var property in type.GetProperties()) Inspect(property.PropertyType);
            }
        }

        foreach (var action in typeof(EmployeesController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Inspect(action.ReturnType);
            foreach (var parameter in action.GetParameters()) Inspect(parameter.ParameterType);
        }

        Assert.Contains(typeof(EmployeeDto), visited);
        Assert.Contains(typeof(CreateEmployeeRequest), visited);
    }
}

/// <summary>The published OpenAPI document is what the frontend team reads: it must describe DTOs only.</summary>
[Collection(PostgresCollection.Name)]
public class EmployeeSwaggerContractTests : IDisposable
{
    private readonly HrmsWebApplicationFactory _factory;

    public EmployeeSwaggerContractTests(PostgresFixture fx)
    {
        _factory = new HrmsWebApplicationFactory(fx);
        _factory.AssertUsesFixtureDatabase();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> SwaggerAsync()
    {
        var response = await _factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Properties(JsonElement schemas, string name)
        => schemas.GetProperty(name).GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();

    [Fact]
    public async Task The_employee_endpoints_are_published_with_their_methods_and_documented_responses()
    {
        var paths = (await SwaggerAsync()).GetProperty("paths");

        var collection = paths.GetProperty("/api/employees");
        Assert.True(collection.TryGetProperty("get", out var list));
        Assert.True(collection.TryGetProperty("post", out var create));
        var item = paths.GetProperty("/api/employees/{id}");
        Assert.True(item.TryGetProperty("get", out _));
        Assert.True(item.TryGetProperty("put", out var update));
        Assert.True(paths.GetProperty("/api/employees/{id}/deactivate").TryGetProperty("put", out var deactivate));

        Assert.True(create.GetProperty("responses").TryGetProperty("201", out _));
        Assert.True(create.GetProperty("responses").TryGetProperty("409", out _));
        Assert.True(list.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(update.GetProperty("responses").TryGetProperty("404", out _));
        Assert.True(deactivate.GetProperty("responses").TryGetProperty("409", out _));
    }

    [Fact]
    public async Task The_list_endpoint_documents_exactly_its_query_parameters()
    {
        var list = (await SwaggerAsync()).GetProperty("paths").GetProperty("/api/employees").GetProperty("get");

        var parameters = list.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "departmentId", "page", "pageSize", "search", "sortBy", "sortDirection", "status" }, parameters);
    }

    [Fact]
    public async Task The_request_schemas_expose_only_the_client_settable_fields()
    {
        var schemas = (await SwaggerAsync()).GetProperty("components").GetProperty("schemas");

        Assert.Equal(new[] { "departmentId", "designation", "email", "employmentType", "joiningDate", "managerId", "name", "personalEmail", "phone" }, Properties(schemas, "CreateEmployeeRequest"));
        Assert.Equal(new[] { "departmentId", "designation", "employmentType", "joiningDate", "lastWorkingDate", "managerId", "name", "personalEmail", "phone", "status" }, Properties(schemas, "UpdateEmployeeRequest"));
        Assert.Equal(new[] { "lastWorkingDate" }, Properties(schemas, "DeactivateEmployeeRequest"));
    }

    [Fact]
    public async Task No_entity_or_credential_shaped_schema_is_published()
    {
        var schemas = (await SwaggerAsync()).GetProperty("components").GetProperty("schemas");
        var names = schemas.EnumerateObject().Select(p => p.Name).ToList();

        foreach (var entity in new[] { "Employee", "User", "Company", "Role", "Department", "Session", "AuditLog" })
        {
            Assert.DoesNotContain(entity, names);
        }

        var employee = Properties(schemas, "EmployeeDto").Select(p => p.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(employee, p => p.Contains("password") || p.Contains("hash") || p.Contains("company") || p == "userid");
        Assert.Contains("empCode", Properties(schemas, "EmployeeDto"));
    }
}

public class EmployeeErrorMappingTests
{
    private static async Task<(int Status, string Body)> RunAsync(Exception toThrow)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddOptions().BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();

        var middleware = new HRMS.API.Middleware.ExceptionHandlingMiddleware(
            _ => throw toThrow,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HRMS.API.Middleware.ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task NotFoundException_maps_to_404_with_only_its_safe_message()
    {
        var (status, body) = await RunAsync(new HRMS.Application.Common.Exceptions.NotFoundException("Employee not found."));

        Assert.Equal(404, status);
        Assert.Equal("{\"message\":\"Employee not found.\"}", body);
    }

    [Fact]
    public async Task ConflictException_maps_to_409_with_only_its_safe_message()
    {
        var (status, body) = await RunAsync(new HRMS.Application.Common.Exceptions.ConflictException("An account with this email already exists."));

        Assert.Equal(409, status);
        Assert.Equal("{\"message\":\"An account with this email already exists.\"}", body);
    }

    [Fact]
    public async Task Unexpected_exceptions_still_return_a_generic_500_without_internal_detail()
    {
        var (status, body) = await RunAsync(new InvalidOperationException("connection string Host=secret;Password=hunter2"));

        Assert.Equal(500, status);
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain("Host=", body);
    }

    [Fact]
    public void The_status_names_are_exactly_the_five_specified_values_and_round_trip()
    {
        var names = Enum.GetValues<HRMS.Domain.Enums.EmployeeStatus>().Select(HRMS.Application.Features.Employees.EmployeeStatusNames.ToApi).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "active", "contract", "inactive", "notice_period", "probation" }, names);
        Assert.All(Enum.GetValues<HRMS.Domain.Enums.EmployeeStatus>(), status =>
        {
            Assert.True(HRMS.Application.Features.Employees.EmployeeStatusNames.TryParse(HRMS.Application.Features.Employees.EmployeeStatusNames.ToApi(status), out var parsed));
            Assert.Equal(status, parsed);
        });
        Assert.False(HRMS.Application.Features.Employees.EmployeeStatusNames.TryParse("retired", out _));
        Assert.False(HRMS.Application.Features.Employees.EmployeeStatusNames.TryParse(null, out _));
    }

    [Fact]
    public void The_temporary_password_generator_is_random_long_and_uses_a_safe_alphabet()
    {
        var generator = new TemporaryPasswordGenerator();
        var passwords = Enumerable.Range(0, 200).Select(_ => generator.Generate()).ToList();

        Assert.Equal(200, passwords.Distinct().Count());
        Assert.All(passwords, p =>
        {
            Assert.Equal(20, p.Length);
            Assert.All(p, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
            Assert.DoesNotContain(p, ch => "0O1lI".Contains(ch));            // no look-alike characters
        });
    }
}
