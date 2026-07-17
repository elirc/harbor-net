using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Harbor.Tests.Integration;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Get_Health_Returns200()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_Health_ReturnsOkStatusAndServiceName()
    {
        var body = await _client.GetFromJsonAsync<HealthPayload>("/health");

        Assert.NotNull(body);
        Assert.Equal("ok", body.Status);
        Assert.Equal("harbor-net", body.Name);
        Assert.False(string.IsNullOrWhiteSpace(body.Version));
        Assert.True(body.UtcNow > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    private sealed record HealthPayload(string Status, string Name, string Version, DateTimeOffset UtcNow);
}
