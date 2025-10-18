using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Agent.Api.Llm;
using FluentAssertions;
using Xunit;

namespace Agent.Api.Tests;

public class ChatEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ChatEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Chat_should_return_ai_message()
    {
        var client = _factory.CreateClient();
        var req = new ChatRequest(new List<ChatMessage>
        {
            new("user", "Hola")
        });
        var resp = await client.PostAsJsonAsync("/chat", req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OllamaChatResponse>();
        body.Should().NotBeNull();
        body!.Message.Should().NotBeNull();
        body.Message!.Role.Should().Be("assistant");
    }
}