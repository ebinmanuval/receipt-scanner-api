using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ReceiptScanner.Tests.Api;

/// <summary>Boots the real app in memory. No database needed for these routes.</summary>
public class ApiHostTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_returns_ok_with_trailing_slash()
    {
        var response = await factory.CreateClient().GetAsync("/api/health/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_route_uses_the_error_envelope()
    {
        var response = await factory.CreateClient().GetAsync("/api/nope/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("error", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("not_found", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("errors").ValueKind);
    }

    [Fact]
    public async Task Wrong_method_uses_the_error_envelope()
    {
        var response = await factory.CreateClient().DeleteAsync("/api/health/");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("method_not_allowed", body.RootElement.GetProperty("code").GetString());
    }
}
