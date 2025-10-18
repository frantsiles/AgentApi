using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Api.Llm;

public class OllamaClient(HttpClient http)
{
    public virtual async Task<OllamaChatResponse> ChatAsync(OllamaChatRequest req, CancellationToken ct = default)
    {
        using var resp = await http.PostAsJsonAsync("/api/chat", req, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }, ct);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Ollama returned null response");
    }
}
