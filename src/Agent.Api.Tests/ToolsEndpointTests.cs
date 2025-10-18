using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Agent.Api.Tests;

public class ToolsEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ToolsEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Tools_filereader_list_should_respond()
    {
        var client = _factory.CreateClient();
        var payload = new { path = "", recursive = false, includeContent = false };
        var resp = await client.PostAsJsonAsync("/tools/filereader-list/invoke", payload);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        body.Should().NotBeNull();
        body!.Should().ContainKey("files");
    }
}