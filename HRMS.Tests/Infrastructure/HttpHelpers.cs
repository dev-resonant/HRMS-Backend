using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HRMS.Application.Features.Auth.Commands.Login;

namespace HRMS.Tests.Infrastructure;

public static class HttpHelpers
{
    public const string TestUserAgent = "HrmsTests/1.0";

    /// <summary>Client whose Host header selects the tenant (the API resolves the tenant from the request host).</summary>
    public static HttpClient ClientFor(this HrmsWebApplicationFactory factory, string domain)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Host = domain;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(TestUserAgent);
        return client;
    }

    public static Task<HttpResponseMessage> PostLoginAsync(this HttpClient client, string email, string password)
        => client.PostAsJsonAsync("/api/auth/login", new { email, password });

    public static async Task<LoginResponseDto> LoginAsync(this HttpClient client, string email, string password)
    {
        var response = await client.PostLoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(body);
        return body!;
    }

    public static Task<HttpResponseMessage> SendAsync(this HttpClient client, HttpMethod method, string url, string? bearerToken)
    {
        var request = new HttpRequestMessage(method, url);

        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> GetMeAsync(this HttpClient client, string? bearerToken)
        => client.SendAsync(HttpMethod.Get, "/api/auth/me", bearerToken);

    public static Task<HttpResponseMessage> LogoutAsync(this HttpClient client, string? bearerToken)
        => client.SendAsync(HttpMethod.Post, "/api/auth/logout", bearerToken);
}
