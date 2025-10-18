using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Agent.Api.Tests;

public class ToolsDirectoryTreeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ToolsDirectoryTreeTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Tools_filereader_tree_should_return_tree()
    {
        var client = _factory.CreateClient();
        var payload = new { path = "", maxDepth = 1, includeFiles = false };
        var resp = await client.PostAsJsonAsync("/tools/filereader-tree/invoke", payload);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        doc.Should().NotBeNull();
        var root = doc!.RootElement;
        root.TryGetProperty("tree", out var tree).Should().BeTrue("response should contain 'tree'");
        tree.ValueKind.Should().Be(JsonValueKind.Object);
        tree.TryGetProperty("type", out var typeProp).Should().BeTrue();
    }
}
