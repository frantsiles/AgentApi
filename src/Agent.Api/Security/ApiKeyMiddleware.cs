using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Agent.Api.Security;

public sealed class ApiKeyOptions
{
    public string? ApiKey { get; set; }
}

public class ApiKeyMiddleware(RequestDelegate next, IOptions<ApiKeyOptions> options, IConfiguration configuration)
{
    private readonly string _apiKey = options.Value.ApiKey ?? configuration.GetValue<string>("ApiKey") ?? string.Empty;

    // Prefer explicit ApiKey property, fallback to configuration root key "ApiKey"

    public async Task InvokeAsync(HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            await next(context);
            return; // no API key configured => skip (development convenience)
        }

        if (!context.Request.Headers.TryGetValue("X-API-Key", out var provided) || provided.Count == 0)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing X-API-Key" });
            return;
        }

        if (!string.Equals(provided[0], _apiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid API key" });
            return;
        }

        await next(context);
    }
}

public static class ApiKeyExtensions
{
    public static IApplicationBuilder UseApiKey(this IApplicationBuilder app)
        => app.UseMiddleware<ApiKeyMiddleware>();
}
