using System.Text;
using System.Text.Json;
using Agent.Api.Llm;

namespace Agent.Api.Planning;

public interface IPlanner
{
    Task<PlanResponse> BuildPlanAsync(PlanRequest request, CancellationToken ct);
}

public class LlmPlanner(OllamaClient ollama) : IPlanner
{
    public async Task<PlanResponse> BuildPlanAsync(PlanRequest request, CancellationToken ct)
    {
        // Instruct LLM to output JSON array of steps with tool and payload
        var system = new ChatMessage("system",
            "You are a meticulous planning assistant for a local AI agent. " +
            "Return a valid JSON object with fields: goal, steps[], and notes. " +
            "Each step: { tool: string, payload: object, description?: string }. " +
            "Use available tools: filereader-list, filewriter-applypatch, pwsh-run, git-diff, git-commit, git-branch, filereader-tree.");
        var user = new ChatMessage("user", $"Goal: {request.Goal}\nContext: {JsonSerializer.Serialize(request.Context ?? new())}\nRespond ONLY with JSON.");

        var response = await ollama.ChatAsync(new OllamaChatRequest
        {
            Messages = new List<ChatMessage> { system, user },
            Stream = false
        }, ct);

        // Try to parse JSON from assistant message
        var content = response.Message?.Content ?? response.Messages?.LastOrDefault()?.Content ?? "{}";
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var steps = new List<PlanStep>();
            if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in stepsEl.EnumerateArray())
                {
                    var tool = s.TryGetProperty("tool", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                    var payload = s.TryGetProperty("payload", out var p) ? p : default;
                    var desc = s.TryGetProperty("description", out var d) ? d.GetString() : null;
                    steps.Add(new PlanStep { Tool = tool, Payload = payload, Description = desc });
                }
            }
            var goal = root.TryGetProperty("goal", out var g) ? g.GetString() ?? request.Goal : request.Goal;
            var notes = root.TryGetProperty("notes", out var n) ? n.GetString() : null;
            return new PlanResponse(goal, steps, notes);
        }
        catch
        {
            // Fallback: return simple single step using chat
            return new PlanResponse(request.Goal, new List<PlanStep>(), "Planner failed to parse model output.");
        }
    }
}
