using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordDockerUpdater.Configuration;
using DiscordDockerUpdater.Hubs;
using DiscordDockerUpdater.Models;
using DiscordDockerUpdater.Services;
using Docker.DotNet;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);

// Configure strongly-typed settings
builder.Services.Configure<BotConfiguration>(
    builder.Configuration.GetSection(BotConfiguration.SectionName));

// Register Docker client (communicates via socket, no CLI needed)
builder.Services.AddSingleton<IDockerClient>(_ =>
{
    Uri dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? new Uri("npipe://./pipe/docker_engine")
        : new Uri("unix:///var/run/docker.sock");
    return new DockerClientConfiguration(dockerUri).CreateClient();
});

var botConfig = builder.Configuration.GetSection(BotConfiguration.SectionName).Get<BotConfiguration>();

if (botConfig?.AgentMode == true)
{
    // Agent mode — Docker services + SignalR client to connect to the bot's hub
    builder.Services.AddSingleton<ContainerInspector>();
    builder.Services.AddSingleton<DockerComposeExecutor>();
    builder.Services.AddSingleton<ComposeFileUpdater>();
    builder.Services.AddHostedService<AgentHubClient>();
}
else
{
    // Bot mode — full Discord + Docker services + SignalR hub for agents
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<AgentConnectionManager>();
    builder.Services.AddSingleton<UpdateTracker>();
    builder.Services.AddSingleton<ContainerInspector>();
    builder.Services.AddSingleton<DiscordNotificationService>();
    builder.Services.AddSingleton<DockerComposeExecutor>();
    builder.Services.AddSingleton<ComposeFileUpdater>();
    builder.Services.AddSingleton<AgentClient>();

    // Discord services
    builder.Services.AddSingleton(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages });
    builder.Services.AddSingleton<DiscordSocketClient>();
    builder.Services.AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>()));
    builder.Services.AddHostedService<DiscordBotService>();
    builder.Services.AddHostedService<StaleUpdateCleanupService>();
}

var app = builder.Build();

// Validate configuration on startup
ValidateConfiguration(app.Services, app.Logger);

// Health check endpoint
app.MapGet("/health", (IServiceProvider sp, IOptions<BotConfiguration> cfg) =>
{
    if (cfg.Value.AgentMode)
    {
        return Results.Ok(new { status = "healthy", mode = "agent" });
    }

    var tracker = sp.GetRequiredService<UpdateTracker>();
    var client = sp.GetRequiredService<DiscordSocketClient>();
    var agents = sp.GetRequiredService<AgentConnectionManager>();
    var connectedAgents = agents.GetConnectedAgents()
        .Select(a => new { a.Registration.Hostname, a.ConnectedAt })
        .ToList();

    return Results.Ok(new
    {
        status = "healthy",
        mode = "bot",
        discord = client.ConnectionState == ConnectionState.Connected ? "connected" : "disconnected",
        pendingUpdates = tracker.GetPendingCount(),
        connectedAgents
    });
})
.WithName("HealthCheck");

// Webhook endpoint for receiving Diun notifications
app.MapPost("/webhook/diun", async (HttpContext httpContext, DiunPayload payload, UpdateTracker tracker,
    DiscordNotificationService notifier, IOptions<BotConfiguration> botConfig, ILogger<Program> logger) =>
{
    // Validate webhook token if configured
    var webhookToken = botConfig.Value.WebhookToken;
    if (!string.IsNullOrWhiteSpace(webhookToken))
    {
        var authHeader = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) ||
            !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            authHeader["Bearer ".Length..] != webhookToken)
        {
            logger.LogWarning("Webhook request rejected: invalid or missing authorization token");
            return Results.Unauthorized();
        }
    }

    // Validate required fields
    if (string.IsNullOrWhiteSpace(payload.Image))
    {
        logger.LogWarning("Received webhook with missing or empty Image field");
        return Results.BadRequest(new { error = "Image field is required" });
    }

    // Check for duplicate (idempotency)
    var existingUpdate = tracker.GetByImageAndDigest(payload.Image, payload.Digest);
    if (existingUpdate != null)
    {
        logger.LogInformation(
            "Duplicate webhook received for image {Image} with digest {Digest}. Existing update ID: {UpdateId}",
            payload.Image,
            payload.Digest,
            existingUpdate.Id);

        return Results.Ok(new
        {
            updateId = existingUpdate.Id,
            receivedAt = existingUpdate.ReceivedAt,
            duplicate = true
        });
    }

    // Add to tracker
    var update = tracker.AddUpdate(payload);

    logger.LogInformation(
        "Received Diun webhook for image {Image} with status {Status}. Update ID: {UpdateId}",
        payload.Image,
        payload.Status,
        update.Id);

    // Post to Discord
    try
    {
        await notifier.NotifyUpdateAvailableAsync(update);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to send Discord notification for update {UpdateId}", update.Id);
        // Don't fail the webhook - the update is tracked regardless
    }

    return Results.Ok(new { updateId = update.Id, receivedAt = update.ReceivedAt });
})
.WithName("DiunWebhook");

// Map SignalR hub for agent connections (bot mode only)
if (botConfig?.AgentMode != true)
{
    app.MapHub<AgentHub>("/agent-hub");
}

app.Run();

/// <summary>
/// Validates configuration on startup and logs warnings/errors for common misconfigurations.
/// Following the fail-fast principle to catch configuration issues early.
/// </summary>
static void ValidateConfiguration(IServiceProvider services, ILogger logger)
{
    var config = services.GetRequiredService<IOptions<BotConfiguration>>().Value;

    if (config.AgentMode)
    {
        logger.LogInformation("Running in agent mode — Discord bot features are disabled");

        if (string.IsNullOrWhiteSpace(config.HubUrl))
        {
            logger.LogCritical(
                "HubUrl is not configured (Bot:HubUrl). " +
                "The agent cannot connect to the bot. " +
                "Set the Bot__HubUrl environment variable to the bot's base URL.");
            throw new InvalidOperationException(
                "HubUrl is not configured. Agent mode requires a hub URL to connect to.");
        }

        if (string.IsNullOrWhiteSpace(config.AgentToken))
        {
            logger.LogWarning(
                "Agent token is not configured (Bot:AgentToken). " +
                "The agent will connect without authentication. " +
                "Set the Bot__AgentToken environment variable for basic security.");
        }

        logger.LogInformation("Configuration validation completed successfully");
        return;
    }

    // Critical: Discord token must be configured (via environment variable or user secrets, not appsettings.json)
    if (string.IsNullOrWhiteSpace(config.DiscordToken))
    {
        logger.LogCritical(
            "Discord bot token is not configured. " +
            "Set the Bot__DiscordToken environment variable or use dotnet user-secrets.");
        throw new InvalidOperationException(
            "Discord bot token is not configured. The application cannot start without a valid token.");
    }

    // Warning: Channel ID should be configured for notifications
    if (config.ChannelId == 0)
    {
        logger.LogWarning(
            "Discord channel ID is not configured (Bot:ChannelId). " +
            "The bot will work but cannot post automatic notifications. " +
            "Only slash commands will be available.");
    }

    if (string.IsNullOrWhiteSpace(config.WebhookToken))
    {
        logger.LogWarning(
            "Webhook token is not configured (Bot:WebhookToken). " +
            "The /webhook/diun endpoint is open to unauthenticated requests. " +
            "Set the Bot__WebhookToken environment variable for basic security.");
    }

    logger.LogInformation("Configuration validation completed successfully");
}
