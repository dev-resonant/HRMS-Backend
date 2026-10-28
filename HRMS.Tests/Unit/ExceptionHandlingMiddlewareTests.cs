using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using HRMS.API.Middleware;
using HRMS.Application.Common.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HRMS.Tests.Unit;

public class ExceptionHandlingMiddlewareTests
{
    private static async Task<(int Status, string Body)> RunAsync(Exception? toThrow)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddOptions().BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            _ => toThrow is null ? Task.CompletedTask : throw toThrow,
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, body);
    }

    [Fact]
    public async Task No_exception_leaves_the_response_untouched()
    {
        var (status, body) = await RunAsync(null);

        Assert.Equal(200, status);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task ValidationException_maps_to_400_with_errors_grouped_by_property()
    {
        var failures = new[] { new ValidationFailure("Email", "bad email"), new ValidationFailure("Password", "required") };

        var (status, body) = await RunAsync(new ValidationException(failures));

        Assert.Equal(400, status);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Validation failed.", json.RootElement.GetProperty("message").GetString());
        Assert.Equal("bad email", json.RootElement.GetProperty("errors").GetProperty("Email")[0].GetString());
        Assert.Equal("required", json.RootElement.GetProperty("errors").GetProperty("Password")[0].GetString());
    }

    [Fact]
    public async Task UnauthorizedAccessException_maps_to_401_with_its_message()
    {
        var (status, body) = await RunAsync(new UnauthorizedAccessException("Invalid email or password."));

        Assert.Equal(401, status);
        Assert.Equal("Invalid email or password.", JsonDocument.Parse(body).RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ForbiddenException_maps_to_403_with_its_message()
    {
        var (status, body) = await RunAsync(new ForbiddenException("Account is deactivated."));

        Assert.Equal(403, status);
        Assert.Equal("Account is deactivated.", JsonDocument.Parse(body).RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Unexpected_exceptions_map_to_500_with_a_generic_body_that_leaks_nothing()
    {
        var (status, body) = await RunAsync(new InvalidOperationException("secret connection detail xyz"));

        Assert.Equal(500, status);
        Assert.Equal("An unexpected error occurred.", JsonDocument.Parse(body).RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("secret connection detail", body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain(" at ", body);              // no stack trace frames
    }
}
