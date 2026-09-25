using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsOkWithExpectedPayload()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        HealthPayload? payload = await response.Content.ReadFromJsonAsync<HealthPayload>();
        payload.ShouldNotBeNull();
        payload.Status.ShouldBe("ok");
    }

    private sealed record HealthPayload(string Status);
}
