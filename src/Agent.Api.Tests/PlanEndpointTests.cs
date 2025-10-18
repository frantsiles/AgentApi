using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Agent.Api.Planning;
using FluentAssertions;
using Xunit;

namespace Agent.Api.Tests;

public class PlanEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PlanEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Plan_should_return_plan_response()
    {
        var client = _factory.CreateClient();
        var req = new PlanRequest("Explorar proyecto", new Dictionary<string, object>());
        var resp = await client.PostAsJsonAsync("/plan", req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PlanResponse>();
        body.Should().NotBeNull();
        body!.Goal.Should().NotBeNullOrEmpty();
        body.Steps.Should().NotBeNull();
    }
}