using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Api.Llm;
using Agent.Api.Planning;
using Agent.Api.Security;
using Agent.Api.Tools;
using Agent.Api.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Bind options
builder.Services.Configure<ApiKeyOptions>(builder.Configuration);

// Add HttpClient for Ollama
builder.Services.AddHttpClient<OllamaClient>((sp, client) =>
{
    var cfg = builder.Configuration.GetSection("Ollama");
    var baseUrl = cfg.GetValue<string>("BaseUrl") ?? "http://localhost:11434";
    client.BaseAddress = new Uri(baseUrl);
});

// Core services
builder.Services.AddSingleton<ToolsRegistry>();

// Tools
builder.Services.AddSingleton<IFileReader, FileReader>();
builder.Services.AddSingleton<IFileTree, FileTreeTool>();
builder.Services.AddSingleton<IFileWriter, FileWriter>();
builder.Services.AddSingleton<IPwshTool, PwshTool>();
builder.Services.AddSingleton<IGitTool, GitTool>();

// Expose tools as ITool for registry discovery
builder.Services.AddSingleton<ITool>(sp => sp.GetRequiredService<IFileReader>());
builder.Services.AddSingleton<ITool>(sp => sp.GetRequiredService<IFileTree>());
builder.Services.AddSingleton<ITool>(sp => sp.GetRequiredService<IFileWriter>());
builder.Services.AddSingleton<ITool>(sp => sp.GetRequiredService<IPwshTool>());
builder.Services.AddSingleton<ITool>(sp => sp.GetRequiredService<IGitTool>());

// Jobs
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService<JobWorker>();

// Planner
builder.Services.AddSingleton<IPlanner, LlmPlanner>();

// Minimal API setup
var app = builder.Build();

app.UseApiKey();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// POST /plan => returns JSON plan
app.MapPost("/plan", async (PlanRequest req, IPlanner planner, CancellationToken ct) =>
{
    var plan = await planner.BuildPlanAsync(req, ct);
    return Results.Ok(plan);
});

// POST /act => enqueue plan steps
app.MapPost("/act", (ActRequest req, JobQueue queue) =>
{
    var id = queue.Enqueue(req.Steps);
    return Results.Accepted($"/jobs/{id}", new { id });
});

// GET /jobs/{id}
app.MapGet("/jobs/{id}", (string id, JobQueue queue) =>
{
    var state = queue.Get(id);
    return state is null ? Results.NotFound() : Results.Ok(state);
});

// POST /chat => pass directly to LLM
app.MapPost("/chat", async (ChatRequest req, OllamaClient ollama, IConfiguration config, CancellationToken ct) =>
{
    var model = config.GetSection("Ollama").GetValue<string>("Model") ?? "llama3.1:8b";
    var res = await ollama.ChatAsync(new OllamaChatRequest
    {
        Model = model,
        Stream = false,
        Messages = req.Messages
    }, ct);
    return Results.Ok(res);
});

// POST /tools/{tool}/invoke => dispatch to tool by name
app.MapPost("/tools/{tool}/invoke", async (string tool, HttpRequest httpReq, ToolsRegistry registry, CancellationToken ct) =>
{
    var json = await JsonDocument.ParseAsync(httpReq.Body, cancellationToken: ct);
    var payload = json.RootElement;
    var toolService = registry.Resolve(tool);
    if (toolService is null)
        return Results.NotFound(new { error = $"Tool '{tool}' not found" });

    var result = await toolService.InvokeAsync(payload, ct);
    return Results.Ok(result);
});

app.Run();

public partial class Program { }

// Request/Response DTOs
namespace Agent.Api.Planning
{
    public record PlanRequest(string Goal, Dictionary<string, object>? Context);
    public record PlanResponse(string Goal, List<PlanStep> Steps, string? Notes);
    public record PlanStep
    {
        public string Tool { get; init; } = string.Empty; // e.g., filereader-list, filewriter-applypatch, pwsh-run, git-diff
        public JsonElement Payload { get; init; }
        public string? Description { get; init; }
    }

    public record ActRequest(List<PlanStep> Steps);
}

namespace Agent.Api.Llm
{
    public record ChatMessage([property: JsonPropertyName("role")] string Role,
                              [property: JsonPropertyName("content")] string Content);

    public record ChatRequest(List<ChatMessage> Messages);

    public record OllamaChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = "llama3.1:8b";
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; init; } = new();
        [JsonPropertyName("stream")] public bool Stream { get; init; } = false;
    }

    public record OllamaChatResponse
    {
        [JsonPropertyName("model")] public string? Model { get; init; }
        [JsonPropertyName("message")] public ChatMessage? Message { get; init; }
        [JsonPropertyName("messages")] public List<ChatMessage>? Messages { get; init; }
        [JsonPropertyName("created_at")] public string? CreatedAt { get; init; }
        [JsonPropertyName("done")] public bool Done { get; init; }
        [JsonPropertyName("eval_count")] public int? EvalCount { get; init; }
    }
}
