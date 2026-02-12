# Discord Docker Updater

A self-hosted Discord bot that integrates with [Diun](https://github.com/crazy-max/diun) (Docker Image Update Notifier) to provide interactive Docker container update management through Discord. Receive webhook notifications when container images are updated, then approve and execute updates directly from Discord with interactive buttons.

## 🚀 Overview

This bot bridges the gap between Diun's update detection and actual container deployment:

1. **Diun** detects that a Docker image has been updated
2. **Diun** sends a webhook to this bot
3. **Bot** posts an interactive message to your Discord channel
4. **You** click "Update" in Discord
5. **Bot** executes `docker compose pull` and `docker compose up -d` for the specified services
6. **You** receive confirmation in Discord

```
┌─────────────────┐         ┌──────────────────────┐         ┌─────────────┐
│                 │ Webhook │                      │ Gateway │             │
│      Diun       ├────────►│ Discord Docker       ├────────►│   Discord   │
│ (Image Monitor) │         │ Updater (This Bot)   │◄────────┤   Server    │
│                 │         │                      │         │             │
└─────────────────┘         └──────────┬───────────┘         └─────────────┘
                                       │
                                       │ Docker Socket
                                       │
                            ┌──────────▼───────────┐
                            │                      │
                            │   Docker Daemon      │
                            │ (Compose Projects)   │
                            │                      │
                            └──────────────────────┘
```

## ✨ Features

- 🔔 **Webhook Endpoint**: Receives Diun notifications via HTTP POST
- 💬 **Discord Integration**: Interactive messages with Update/Dismiss button actions
- 🐳 **Docker Compose Management**: Execute pull and up commands automatically
- 🔍 **Auto-Discovery**: Automatically resolves compose projects via Docker socket labels — no manual mapping needed
- 🔒 **Update Tracking**: Prevent duplicate notifications and track update status
- 🧹 **Stale Cleanup**: Background service removes old pending updates (configurable retention)
- 📊 **Structured Logging**: Comprehensive logging for debugging and monitoring

## 📋 Prerequisites

- **Docker** and **Docker Compose** (v2+)
- **Discord Bot Token** (see [Configuration](#discord-bot-setup))
- **Diun** running and configured with webhooks
- A Discord server where you have permission to add bots

## 🏃 Quick Start

### 1. Clone the Repository

```bash
git clone https://github.com/yourusername/discord-docker-updater.git
cd discord-docker-updater
```

### 2. Configure Environment Variables

Create a `.env` file in the repository root:

```env
DISCORD_TOKEN=your_discord_bot_token_here
DISCORD_CHANNEL_ID=your_discord_channel_id_here
```

> **Note:** The bot token is passed via the `Bot__DiscordToken` environment variable. Never commit tokens to source control — use `.env` files, Docker secrets, or your orchestrator's secret management.

### 3. Deploy

```bash
docker compose up -d
```

Check logs:

```bash
docker compose logs -f discord-docker-updater
```

## ⚙️ Configuration

### Discord Bot Setup

1. **Create a Discord Application**:
   - Go to [Discord Developer Portal](https://discord.com/developers/applications)
   - Click "New Application"
   - Give it a name (e.g., "Docker Updater")

2. **Create a Bot**:
   - Navigate to the "Bot" tab
   - Click "Add Bot"
   - Under "Privileged Gateway Intents", enable:
     - ✅ SERVER MEMBERS INTENT
     - ✅ MESSAGE CONTENT INTENT
   - Click "Reset Token" and copy your bot token
   - Save this token to your `.env` file as `DISCORD_TOKEN`

3. **Invite the Bot to Your Server**:
   - Navigate to the "OAuth2" → "URL Generator" tab
   - Select scopes:
     - ✅ `bot`
     - ✅ `applications.commands`
   - Select bot permissions:
     - ✅ Send Messages
     - ✅ Embed Links
     - ✅ Read Message History
     - ✅ Use Slash Commands
   - Copy the generated URL and open it in your browser
   - Select your server and authorize

4. **Get Your Channel ID**:
   - In Discord, enable Developer Mode: User Settings → Advanced → Developer Mode
   - Right-click the channel where you want notifications → Copy ID
   - Save this ID to your `.env` file as `DISCORD_CHANNEL_ID`

### Configuration Reference

All settings live under the `Bot` section and can be set via environment variables using the `Bot__` prefix:

| Setting | Env Variable | Description | Default |
|---------|-------------|-------------|---------|
| `DiscordToken` | `Bot__DiscordToken` | Discord bot token (required) | — |
| `ChannelId` | `Bot__ChannelId` | Discord channel ID for notifications (required) | — |
| `StaleUpdateRetentionDays` | `Bot__StaleUpdateRetentionDays` | Days to keep pending updates before cleanup | `7` |

### Diun Configuration

Configure Diun to send webhooks to this bot. Example `diun.yml`:

```yaml
notif:
  webhook:
    endpoint: http://discord-docker-updater:8080/webhook/diun
    method: POST
    headers:
      Content-Type: application/json
    timeout: 10s

watch:
  workers: 10
  schedule: "0 */6 * * *"  # Check every 6 hours

providers:
  docker:
    watchByDefault: true
```

If Diun is on the same Docker network:

```yaml
# In your Diun compose file
networks:
  - discord-updater-network

# In discord-docker-updater compose file
services:
  discord-docker-updater:
    networks:
      - discord-updater-network

networks:
  discord-updater-network:
    external: true
```

Or use host network mode / expose the port and use `http://<host-ip>:8080/webhook/diun`.

### Auto-Discovery via Docker Socket

The bot **automatically discovers** which Docker Compose project a container belongs to by inspecting labels on running containers via the Docker socket. This works the same way [Watchtower](https://containrrr.dev/watchtower/) does — no manual project mapping required.

When Diun sends a notification about a container, the bot runs `docker inspect` and reads these standard Docker Compose labels:

| Label | Purpose |
|-------|---------|
| `com.docker.compose.project.working_dir` | Directory containing the compose file |
| `com.docker.compose.project.config_files` | Compose file path(s) |
| `com.docker.compose.service` | Service name within the project |
| `com.docker.compose.project` | Project name |

These labels are set automatically by Docker Compose on every container it manages. The bot then executes:

```bash
docker compose -f <config_file> pull <service>
docker compose -f <config_file> up -d <service>
```

> **Requirement:** The bot container must have the Docker socket mounted (`/var/run/docker.sock`) and the target compose files must be accessible from the host (which they are, since the bot runs compose commands on the host's Docker daemon).

## 🏗️ Architecture

### Technology Stack

- **.NET 10.0** - Latest .NET framework with native AOT ready
- **ASP.NET Core Minimal API** - Lightweight HTTP server for webhooks
- **Discord.Net 3.18.0** - Comprehensive Discord bot framework
- **Docker CLI** - Installed in container for compose command execution

### Project Structure

```
DiscordDockerUpdater.slnx
├── src/
│   └── DiscordDockerUpdater/
│       ├── DiscordDockerUpdater.csproj
│       ├── Program.cs                        # Application entry point & webhook endpoint
│       ├── Dockerfile                        # Multi-stage Docker build
│       ├── appsettings.json                  # Configuration template
│       ├── Configuration/
│       │   └── BotConfiguration.cs           # Strongly-typed config
│       ├── Services/
│       │   ├── ContainerInspector.cs         # Auto-discover compose info via Docker socket
│       │   ├── DiscordBotService.cs          # Discord gateway bot (IHostedService)
│       │   ├── DiscordNotificationService.cs # Message formatting & sending
│       │   ├── DockerComposeExecutor.cs      # Execute docker compose commands
│       │   ├── StaleUpdateCleanupService.cs  # Background cleanup of old pending updates
│       │   └── UpdateTracker.cs              # Track update state
│       ├── Models/
│       │   └── DiunPayload.cs                # Diun webhook JSON model
│       └── Modules/
│           └── UpdateModule.cs               # Discord slash command module
├── tests/
│   └── DiscordDockerUpdater.Tests/
│       ├── DiscordDockerUpdater.Tests.csproj
│       ├── Models/
│       │   └── DiunPayloadTests.cs
│       └── Services/
│           ├── DockerComposeExecutorTests.cs
│           └── UpdateTrackerTests.cs
├── docker-compose.yml                        # Deployment compose file
├── .dockerignore
├── LICENSE
└── README.md
```

### How It Works

1. **ASP.NET Minimal API** runs on port 8080, hosting the `/webhook/diun` endpoint
2. **DiscordBotService** runs as an `IHostedService`, maintaining the Discord gateway connection
3. **StaleUpdateCleanupService** runs as a `BackgroundService`, periodically removing old pending updates
4. When Diun POSTs to `/webhook/diun`:
   - Parse `DiunPayload` from JSON
   - Use `UpdateTracker` to check for duplicates (same image + digest)
   - Use `DiscordNotificationService` to send an interactive embed with Update/Dismiss buttons
5. When user clicks "Update" button in Discord:
   - `DiscordBotService` handles the button interaction
   - `ContainerInspector` runs `docker inspect` to discover the container's compose project
   - `DockerComposeExecutor` runs `docker compose pull` and `docker compose up -d`
   - The Discord message is updated with a success/failure result embed

## 🛠️ Development

### Prerequisites

- .NET 10 SDK
- Docker Desktop (for local testing)
- Visual Studio 2022 / VS Code / Rider

### Building Locally

```bash
# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Run tests
dotnet test

# Run the application
cd src/DiscordDockerUpdater
dotnet run
```

### Running with Docker

```bash
# Build the image
docker compose build

# Run the container
docker compose up -d

# View logs
docker compose logs -f

# Stop and remove
docker compose down
```

### Testing the Webhook

Send a test payload to the webhook endpoint:

```bash
curl -X POST http://localhost:8080/webhook/diun \
  -H "Content-Type: application/json" \
  -d '{
    "diun_version": "4.28.0",
    "hostname": "my-server",
    "status": "new",
    "provider": "docker",
    "image": "ghcr.io/linuxserver/plex:latest",
    "hub_link": "https://ghcr.io/linuxserver/plex",
    "mime_type": "application/vnd.oci.image.manifest.v1+json",
    "digest": "sha256:abc123...",
    "created": "2026-01-15T10:30:00Z",
    "platform": "linux/amd64",
    "metadata": {
      "ctn_names": "plex",
      "ctn_id": "abc123def456",
      "ctn_status": "running",
      "ctn_state": "running"
    }
  }'
```

> **Note:** The `metadata.ctn_names` field is the container name that the bot uses to look up compose information via `docker inspect`.

## 🐛 Troubleshooting

### Bot doesn't connect to Discord

- Verify your `DISCORD_TOKEN` is correct
- Check bot has required gateway intents enabled
- Check logs: `docker compose logs discord-docker-updater`

### Webhook not received

- Verify port 8080 is accessible from Diun container
- Check Docker network configuration
- Test with curl from Diun container: `curl http://discord-docker-updater:8080/webhook/diun`

### Docker commands fail

- Verify Docker socket is mounted: `/var/run/docker.sock:/var/run/docker.sock`
- Check container has permission to access Docker socket
- Ensure the target container is managed by Docker Compose (has compose labels)
- Run `docker inspect <container> --format '{{json .Config.Labels}}'` to verify compose labels exist

### No message appears in Discord

- Verify `DISCORD_CHANNEL_ID` is correct
- Check bot has permission to send messages in that channel
- Verify bot is a member of the server
- Check Update Tracker - might already have been notified for this update

## 📝 License

This project is licensed under the [MIT License](LICENSE).

## 🙏 Acknowledgments

- [Diun](https://github.com/crazy-max/diun) - Docker Image Update Notifier
- [Discord.Net](https://github.com/discord-net/Discord.Net) - Discord API wrapper
- [ASP.NET Core](https://github.com/dotnet/aspnetcore) - Web framework

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

---

**Built with ❤️ using .NET 10 and modern C# best practices**