# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/DiscordDockerUpdater/DiscordDockerUpdater.csproj", "src/DiscordDockerUpdater/"]
RUN dotnet restore "src/DiscordDockerUpdater/DiscordDockerUpdater.csproj"
COPY . .
WORKDIR "/src/src/DiscordDockerUpdater"
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=30s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "DiscordDockerUpdater.dll"]