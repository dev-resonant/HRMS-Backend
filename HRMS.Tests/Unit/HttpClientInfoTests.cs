using System.Net;
using HRMS.API.Services;
using Microsoft.AspNetCore.Http;

namespace HRMS.Tests.Unit;

public class HttpClientInfoTests
{
    private static HttpClientInfo Create(IPAddress? remote, string? userAgent = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote;

        if (userAgent is not null)
        {
            context.Request.Headers.UserAgent = userAgent;
        }

        return new HttpClientInfo(new HttpContextAccessor { HttpContext = context });
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]                  // plain IPv4 unchanged
    [InlineData("2001:db8::1", "2001:db8::1")]                  // plain IPv6 unchanged
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]           // IPv4-mapped IPv6 -> IPv4
    [InlineData("fe80::1%12", "fe80::1")]                       // IPv6 scope id removed
    public void IpAddress_is_normalized(string input, string expected)
    {
        Assert.Equal(expected, Create(IPAddress.Parse(input)).IpAddress);
    }

    [Fact]
    public void IpAddress_is_null_when_there_is_no_remote_address_or_no_http_context()
    {
        Assert.Null(Create(null).IpAddress);
        Assert.Null(new HttpClientInfo(new HttpContextAccessor { HttpContext = null }).IpAddress);
    }

    [Fact]
    public void Normalized_addresses_always_fit_the_45_character_column()
    {
        // The longest textual forms: full IPv6 and an IPv6 with a long scope id.
        var longest = Create(IPAddress.Parse("2001:db8:85a3:8d3:1319:8a2e:370:7348")).IpAddress;
        var scoped = Create(IPAddress.Parse("fe80::1234:5678:9abc:def0%123456")).IpAddress;

        Assert.NotNull(longest);
        Assert.NotNull(scoped);
        Assert.True(longest!.Length <= 45);
        Assert.DoesNotContain('%', scoped!);
    }

    [Fact]
    public void UserAgent_is_returned_as_is_when_short_and_truncated_to_512_characters_when_long()
    {
        Assert.Equal("Mozilla/5.0 test", Create(null, "Mozilla/5.0 test").UserAgent);
        Assert.Equal(512, Create(null, new string('a', 600)).UserAgent!.Length);
    }

    [Fact]
    public void UserAgent_is_null_when_missing_or_blank()
    {
        Assert.Null(Create(null).UserAgent);
        Assert.Null(Create(null, "   ").UserAgent);
    }
}
