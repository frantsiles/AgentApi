using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Agent.Api.Llm;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Api.Tests;

public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["ApiKey"] = "", // disable API key requirement during tests
                ["Ollama:BaseUrl"] = "http://localhost:0", // not used because we replace the client
            };
            config.AddInMemoryCollection(dict!);
        });

        builder.ConfigureServices(services =>
        {
            // Replace OllamaClient with a fake to avoid external HTTP calls
            services.AddSingleton<OllamaClient, FakeOllamaClient>();
        });
    }

    private class FakeOllamaClient() : OllamaClient(new HttpClient())
    {
        public override Task<OllamaChatResponse> ChatAsync(OllamaChatRequest req, CancellationToken ct = default)
        {
            var response = new OllamaChatResponse
            {
                Model = req.Model,
                Message = new ChatMessage("assistant", "Respuesta de prueba"),
                Done = true,
                EvalCount = 1
            };
            return Task.FromResult(response);
        }
    }
}