using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Agent.Api.Planning;
using Agent.Api.Workers;
using FluentAssertions;
using Xunit;

namespace Agent.Api.Tests;

public class ActAndJobsEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ActAndJobsEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Act_should_enqueue_and_job_should_complete()
    {
        var client = _factory.CreateClient();

        // Create a simple step that lists current directory (safe)
        using var payloadDoc = JsonDocument.Parse("{}\n");
        var step = new PlanStep { Tool = "filereader-list", Payload = payloadDoc.RootElement, Description = "List root" };
        var act = new ActRequest(new List<PlanStep> { step });

        var resp = await client.PostAsJsonAsync("/act", act);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var json = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        json.Should().NotBeNull();
        json!.Should().ContainKey("id");
        var id = json["id"];

        // Poll job status until Done or timeout
        var started = DateTime.UtcNow;
        JobState? state = null;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(10))
        {
            var jobResp = await client.GetAsync($"/jobs/{id}");
            jobResp.StatusCode.Should().Be(HttpStatusCode.OK);
            state = await jobResp.Content.ReadFromJsonAsync<JobState>();
            state.Should().NotBeNull();
            if (state!.Status is JobStatus.Done or JobStatus.Error) break;
            await Task.Delay(200);
        }

        state!.Status.Should().Be(JobStatus.Done);
        state.Logs.Should().NotBeNull();
    }
}