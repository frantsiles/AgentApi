# Multi-stage build for Agent.Api
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files
COPY AgentApi.sln ./
COPY src/Agent.Api/Agent.Api.csproj src/Agent.Api/

# Restore
RUN dotnet restore AgentApi.sln

# Copy everything
COPY . .

# Publish
RUN dotnet publish src/Agent.Api/Agent.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Expose HTTP port
ENV ASPNETCORE_URLS=http://+:9090
EXPOSE 8080

# Default config values can be overridden by environment variables
# e.g., ApiKey, Agent__RootPath, Ollama__BaseUrl, PowerShell__AllowedCommands__0, etc.

ENTRYPOINT ["dotnet", "Agent.Api.dll"]
