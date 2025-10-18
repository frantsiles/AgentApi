# AgentApi

An ASP.NET Core 9.0 Agent API that orchestrates a local AI development assistant. It exposes endpoints to plan actions, run tools (file read/write, PowerShell, Git), chat with an LLM via Ollama, and execute plans asynchronously via a background job queue.

## Project layout

- AgentApi.sln
- src/Agent.Api/
  - Program.cs
  - appsettings.json
  - Security/
  - Llm/
  - Planning/
  - Tools/
  - Workers/
  - Dockerfile
- docker-compose.yml

## Requirements

- .NET 9.0 SDK
- Windows PowerShell Core (pwsh) available in PATH (on Windows host)
- Ollama running at http://localhost:11434 (or start via docker-compose)

## Configuration

Edit `src/Agent.Api/appsettings.json`:
- ApiKey: X-API-Key for requests
- Agent.RootPath: Absolute path of the repository/workspace to operate on
- Ollama.BaseUrl, Ollama.Model
- PowerShell.AllowedCommands: list of first tokens allowed to run

You can override via environment variables, e.g. `Agent__RootPath`.

## Run locally

```bash
cd src/Agent.Api
dotnet run
```

By default the API listens on the default ASP.NET port. The Docker image exposes 8080.

## Docker

Build and run with compose:

```bash
docker compose up --build
```

The API is served on http://localhost:8080 and Ollama on http://localhost:11434.

## Security

All endpoints require header `X-API-Key` if configured. Set `ApiKey` in appsettings or via env.

## Endpoints

- GET /health
- POST /plan
- POST /act
- GET /jobs/{id}
- POST /tools/{tool}/invoke
- POST /chat

### Examples (PowerShell)

Replace the API key with your value (default in repo is `change-me`).

- Health
```powershell
curl -Method GET http://localhost:8080/health -Headers @{ 'X-API-Key'='change-me' }
```

- Plan
```powershell
$body = @{ goal = 'Add /health endpoint'; context = @{ project = 'AgentApi' } } | ConvertTo-Json
curl -Method POST http://localhost:8080/plan -Headers @{ 'Content-Type'='application/json'; 'X-API-Key'='change-me' } -Body $body
```

- Act
```powershell
$steps = @(
  @{ tool = 'filereader-list'; payload = @{ path='src/Agent.Api'; recursive=$true; includeContent=$false }; description='List files' },
  @{ tool = 'pwsh-run'; payload = @{ command='dotnet --info'; timeoutSeconds=30 }; description='Check dotnet' }
)
$body = @{ steps = $steps } | ConvertTo-Json -Depth 10
curl -Method POST http://localhost:8080/act -Headers @{ 'Content-Type'='application/json'; 'X-API-Key'='change-me' } -Body $body
```

- Job status
```powershell
curl -Method GET http://localhost:8080/jobs/{id} -Headers @{ 'X-API-Key'='change-me' }
```

- Invoke a tool (FileReader)
```powershell
$body = @{ path='src/Agent.Api'; recursive=$true; includeContent=$false } | ConvertTo-Json
curl -Method POST http://localhost:8080/tools/filereader-list/invoke -Headers @{ 'Content-Type'='application/json'; 'X-API-Key'='change-me' } -Body $body
```

- Invoke a tool (FileWriter)
```powershell
$ops = @(
  @{ op='write'; path='temp/example.txt'; content='hello world' }
) 
$body = @{ operations=$ops } | ConvertTo-Json -Depth 5
curl -Method POST http://localhost:8080/tools/filewriter-applypatch/invoke -Headers @{ 'Content-Type'='application/json'; 'X-API-Key'='change-me' } -Body $body
```

- Invoke PowerShell (allowlisted)
```powershell
$body = @{ command='dotnet --info'; timeoutSeconds=30 } | ConvertTo-Json
curl -Method POST http://localhost:8080/tools/pwsh-run/invoke -Headers @{ 'Content-Type'='application/json'; 'X-API-Key'='change-me' } -Body $body
```

- Git diff
```powershell
$body = @{ op='diff' } | ConvertTo-Json
curl -Method POST http://localhost:8080/tools/git/invoke -Headers @{ 'Content-Type'='application/json'; 'X-API-Key'='change-me' } -Body $body
```

- Chat
```powershell
$messages = @(
  @{ role='system'; content='You are a helpful assistant.' },
  @{ role='user'; content='Say hi' }
)
$body = @{ messages=$messages } | ConvertTo-Json -Depth 10
curl -Method POST http://localhost:8080/chat -Headers @{ 'Content-Type'='application/json'; 'X-API-Key'='change-me' } -Body $body
```

- Invoke a tool (FileTree)
```powershell
# Returns a directory tree from Agent.RootPath (or a subpath)
$body = @{ path=''; maxDepth=2; includeFiles=$true; includeSizes=$false } | ConvertTo-Json
curl -Method POST http://localhost:8080/tools/filereader-tree/invoke -Headers @{ 'Content-Type'='application/json'; 'X-API-Key'='change-me' } -Body $body
```

## Notes
- File operations are restricted to the configured agent root path.
- PowerShell runs the command only if its first token is allowlisted.
- Background worker processes jobs in sequence to avoid file conflicts.
