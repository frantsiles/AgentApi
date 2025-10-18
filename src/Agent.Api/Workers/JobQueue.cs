using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Text.Json;
using Agent.Api.Planning;
using Agent.Api.Tools;

namespace Agent.Api.Workers;

public enum JobStatus
{
    Queued,
    Running,
    Done,
    Error
}

public class JobState
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Logs { get; } = new();
}

public class JobQueue
{
    private readonly Channel<(JobState Job, List<PlanStep> Steps)> _channel = Channel.CreateUnbounded<(JobState, List<PlanStep>)>();
    private readonly ConcurrentDictionary<string, JobState> _states = new();

    public string Enqueue(List<PlanStep> steps)
    {
        var job = new JobState();
        _states[job.Id] = job;
        _channel.Writer.TryWrite((job, steps));
        return job.Id;
    }

    public JobState? Get(string id) => _states.TryGetValue(id, out var s) ? s : null;

    internal ChannelReader<(JobState Job, List<PlanStep> Steps)> Reader => _channel.Reader;
}

public class JobWorker(JobQueue queue, ToolsRegistry tools) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (job, steps) in queue.Reader.ReadAllAsync(stoppingToken))
        {
            job.Status = JobStatus.Running;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            try
            {
                foreach (var step in steps)
                {
                    var name = step.Tool;
                    var tool = tools.Resolve(name);
                    if (tool is null)
                    {
                        job.Logs.Add($"[ERROR] Tool not found: {name}");
                        job.Status = JobStatus.Error;
                        break;
                    }
                    job.Logs.Add($"[INFO] Executing {name} ...");
                    var result = await tool.InvokeAsync(step.Payload, stoppingToken);
                    var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
                    job.Logs.Add($"[RESULT] {json}");
                    job.UpdatedAt = DateTimeOffset.UtcNow;
                }
                if (job.Status != JobStatus.Error)
                {
                    job.Status = JobStatus.Done;
                }
            }
            catch (Exception ex)
            {
                job.Logs.Add($"[EXCEPTION] {ex.Message}\n{ex}");
                job.Status = JobStatus.Error;
            }
            finally
            {
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
    }
}
