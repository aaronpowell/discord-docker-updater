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
WORKDIR /app

# Install Docker CLI (needed for docker compose commands)
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates curl && \
    install -m 0755 -d /etc/apt/keyrings && \
    curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc && \
    chmod a+r /etc/apt/keyrings/docker.asc && \
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian $(. /etc/os-release && echo \"\$VERSION_CODENAME\") stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null && \
    apt-get update && \
    apt-get install -y --no-install-recommends docker-ce-cli docker-compose-plugin && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "DiscordDockerUpdater.dll"]