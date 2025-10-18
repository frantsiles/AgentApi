using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;

namespace Agent.Api.Tools;

public interface ITool
{
    string Name { get; }
    Task<object> InvokeAsync(JsonElement payload, CancellationToken ct);
}

public class ToolsRegistry
{
    private readonly IReadOnlyDictionary<string, ITool> _byName;

    public ToolsRegistry(IEnumerable<ITool> tools)
    {
        _byName = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    public ITool? Resolve(string name) => _byName.TryGetValue(name, out var t) ? t : null;
}

// ---------- File Reader ----------
public interface IFileReader : ITool { }

public class FileReader(IConfiguration cfg) : IFileReader
{
    private readonly string _root = cfg.GetSection("Agent").GetValue<string>("RootPath") ?? Directory.GetCurrentDirectory();

    public string Name => "filereader-list";

    public async Task<object> InvokeAsync(JsonElement payload, CancellationToken ct)
    {
        var rel = payload.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty;
        var recursive = payload.TryGetProperty("recursive", out var r) && r.GetBoolean();
        var includeContent = payload.TryGetProperty("includeContent", out var ic) && ic.GetBoolean();
        var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (payload.TryGetProperty("ignore", out var ig) && ig.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in ig.EnumerateArray()) ignore.Add(i.GetString() ?? string.Empty);
        }

        var target = Path.GetFullPath(Path.Combine(_root, rel));
        if (!target.StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Access outside of root is not allowed");

        var files = new List<object>();
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        if (Directory.Exists(target))
        {
            foreach (var file in Directory.EnumerateFiles(target, "*", option))
            {
                if (ignore.Any(x => file.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                files.Add(await DescribeFileAsync(file, includeContent, ct));
            }
        }
        else if (File.Exists(target))
        {
            files.Add(await DescribeFileAsync(target, includeContent, ct));
        }
        else
        {
            return new { error = "path not found", path = rel };
        }

        return new { root = _root, path = rel, files };
    }

    private static async Task<object> DescribeFileAsync(string path, bool includeContent, CancellationToken ct)
    {
        var fi = new FileInfo(path);
        string? content = null;
        if (includeContent && fi.Length <= 512 * 1024) // cap at 512KB
        {
            using var fs = File.OpenRead(path);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            content = await sr.ReadToEndAsync(ct);
        }
        return new { path = Path.GetRelativePath(Directory.GetCurrentDirectory(), path), size = fi.Length, modified = fi.LastWriteTimeUtc, content };
    }
}

// ---------- File Writer ----------
public interface IFileWriter : ITool { }

public class FileWriter(IConfiguration cfg) : IFileWriter
{
    private readonly string _root = cfg.GetSection("Agent").GetValue<string>("RootPath") ?? Directory.GetCurrentDirectory();

    public string Name => "filewriter-applypatch";

    public Task<object> InvokeAsync(JsonElement payload, CancellationToken ct)
    {
        // payload: { operations: [ { op: write|append|delete, path: string, content?: string } ] }
        var operations = payload.GetProperty("operations").EnumerateArray();
        var results = new List<object>();
        foreach (var op in operations)
        {
            var kind = op.GetProperty("op").GetString() ?? "";
            var rel = op.GetProperty("path").GetString() ?? "";
            var content = op.TryGetProperty("content", out var c) ? c.GetString() : null;
            var target = Path.GetFullPath(Path.Combine(_root, rel));
            if (!target.StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Access outside of root is not allowed");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            switch (kind.ToLowerInvariant())
            {
                case "write":
                    File.WriteAllText(target, content ?? string.Empty, Encoding.UTF8);
                    results.Add(new { path = rel, status = "written" });
                    break;
                case "append":
                    File.AppendAllText(target, content ?? string.Empty, Encoding.UTF8);
                    results.Add(new { path = rel, status = "appended" });
                    break;
                case "delete":
                    if (File.Exists(target)) File.Delete(target);
                    results.Add(new { path = rel, status = "deleted" });
                    break;
                default:
                    results.Add(new { path = rel, status = "error", error = "Unknown op" });
                    break;
            }
        }
        return Task.FromResult<object>(new { ok = true, results });
    }
}

// ---------- PowerShell Tool ----------
public interface IPwshTool : ITool { }

public class PwshTool(IConfiguration cfg) : IPwshTool
{
    public string Name => "pwsh-run";

    public async Task<object> InvokeAsync(JsonElement payload, CancellationToken ct)
    {
        var cmd = payload.GetProperty("command").GetString() ?? string.Empty;
        var timeoutSec = payload.TryGetProperty("timeoutSeconds", out var t) ? t.GetInt32() : cfg.GetSection("PowerShell").GetValue<int>("DefaultTimeoutSeconds", 60);
        var waitForExit = !payload.TryGetProperty("waitForExit", out var wfe) || (wfe.ValueKind == JsonValueKind.True); // default true
        if (!IsAllowed(cmd))
        {
            return new { error = "Command not allowed" };
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-NoLogo -NoProfile -Command \"{cmd}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = waitForExit,
            RedirectStandardError = waitForExit
        };

        using var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        if (!waitForExit)
        {
            // Fire-and-forget: start and return immediately with PID
            var started = proc.Start();
            return new
            {
                command = cmd,
                started,
                pid = started ? (int?)proc.Id : null,
                waitForExit,
                note = "Process started in background; no stdout/stderr captured."
            };
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        try
        {
            await Task.Run(() => proc.WaitForExit(), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timeout or external cancellation; try to kill the process tree
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
        }

        var exitCode = proc.HasExited ? proc.ExitCode : -1;
        return new
        {
            command = cmd,
            exitCode,
            waitForExit,
            stdout = stdout.ToString(),
            stderr = stderr.ToString()
        };
    }

    private bool IsAllowed(string command)
    {
        var allowed = cfg.GetSection("PowerShell").GetSection("AllowedCommands").Get<string[]>() ?? Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(command)) return false;
        var firstToken = command.Split(' ', '\t', '\r', '\n').FirstOrDefault() ?? string.Empty;
        return allowed.Any(a => string.Equals(a, firstToken, StringComparison.OrdinalIgnoreCase));
    }
}

// ---------- Git Tool ----------
public interface IGitTool : ITool { }

public class GitTool(IConfiguration cfg) : IGitTool
{
    private readonly string _root = cfg.GetSection("Agent").GetValue<string>("RootPath") ?? Directory.GetCurrentDirectory();
    public string Name => "git"; // dispatch on subcommand in payload

    public Task<object> InvokeAsync(JsonElement payload, CancellationToken ct)
    {
        var sub = payload.TryGetProperty("op", out var o) ? o.GetString() ?? string.Empty : string.Empty;
        return sub switch
        {
            "diff" => Task.FromResult<object>(Diff()),
            "commit" => Task.FromResult<object>(Commit(payload)),
            "branch" => Task.FromResult<object>(Branch(payload)),
            _ => Task.FromResult<object>(new { error = "Unknown git op" })
        };
    }

    private object Diff()
    {
        using var repo = new Repository(_root);
        var changes = repo.Diff.Compare<TreeChanges>(repo.Head.Tip.Tree, DiffTargets.Index | DiffTargets.WorkingDirectory);
        var entries = changes.Select(c => new { path = c.Path, status = c.Status.ToString() }).ToList();
        return new { count = entries.Count, entries };
    }

    private object Commit(JsonElement payload)
    {
        var message = payload.TryGetProperty("message", out var m) ? m.GetString() ?? "update" : "update";
        using var repo = new Repository(_root);
        Commands.Stage(repo, "*");
        var author = new Signature("Agent", "agent@example.com", DateTimeOffset.Now);
        var committer = author;
        if (!repo.Index.Any()) return new { error = "Nothing to commit" };
        var commit = repo.Commit(message, author, committer);
        return new { sha = commit.Sha, message = commit.MessageShort };
    }

    private object Branch(JsonElement payload)
    {
        var name = payload.TryGetProperty("name", out var n) ? n.GetString() ?? "agent-branch" : "agent-branch";
        using var repo = new Repository(_root);
        var branch = repo.Branches[name] ?? repo.CreateBranch(name);
        Commands.Checkout(repo, branch);
        return new { branch = branch.FriendlyName, tip = branch.Tip?.Sha };
    }
}


// ---------- File Tree (new) ----------
public interface IFileTree : ITool { }

public class FileTreeTool(IConfiguration cfg) : IFileTree
{
    private readonly string _root = cfg.GetSection("Agent").GetValue<string>("RootPath") ?? Directory.GetCurrentDirectory();

    public string Name => "filereader-tree";

    public Task<object> InvokeAsync(JsonElement payload, CancellationToken ct)
    {
        var rel = payload.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty;
        var includeFiles = !payload.TryGetProperty("includeFiles", out var ifel) || ifel.GetBoolean();
        var includeSizes = payload.TryGetProperty("includeSizes", out var isz) && isz.GetBoolean();
        var maxDepth = payload.TryGetProperty("maxDepth", out var md) && md.ValueKind == JsonValueKind.Number ? Math.Max(0, md.GetInt32()) : 5;

        var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (payload.TryGetProperty("ignore", out var ig) && ig.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in ig.EnumerateArray()) ignore.Add(i.GetString() ?? string.Empty);
        }

        var target = Path.GetFullPath(Path.Combine(_root, rel));
        if (!target.StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Access outside of root is not allowed");

        if (!Directory.Exists(target) && !File.Exists(target))
        {
            return Task.FromResult<object>(new { error = "path not found", path = rel });
        }

        var node = BuildNode(target, 0, maxDepth, includeFiles, includeSizes, ignore);
        return Task.FromResult<object>(new { root = _root, path = rel, tree = node });
    }

    private static TreeNode BuildNode(string fullPath, int depth, int maxDepth, bool includeFiles, bool includeSizes, HashSet<string> ignore)
    {
        bool isDir = Directory.Exists(fullPath);
        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name)) name = new DirectoryInfo(fullPath).Name;
        var node = new TreeNode
        {
            Name = name,
            Path = fullPath,
            Type = isDir ? "dir" : "file",
            Size = includeSizes && !isDir ? new FileInfo(fullPath).Length : null,
            Children = isDir ? new List<TreeNode>() : null
        };

        if (isDir && depth < maxDepth)
        {
            foreach (var dir in SafeEnumDirectories(fullPath))
            {
                if (ignore.Any(x => dir.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                node.Children!.Add(BuildNode(dir, depth + 1, maxDepth, includeFiles, includeSizes, ignore));
            }
            if (includeFiles)
            {
                foreach (var file in SafeEnumFiles(fullPath))
                {
                    if (ignore.Any(x => file.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                    node.Children!.Add(BuildNode(file, depth + 1, maxDepth, includeFiles, includeSizes, ignore));
                }
            }
        }

        return node;
    }

    private static IEnumerable<string> SafeEnumDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return Array.Empty<string>(); }
    }
    private static IEnumerable<string> SafeEnumFiles(string path)
    {
        try { return Directory.EnumerateFiles(path); }
        catch { return Array.Empty<string>(); }
    }

    private class TreeNode
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Type { get; set; } = "file"; // or "dir"
        public long? Size { get; set; }
        public List<TreeNode>? Children { get; set; }
    }
}
