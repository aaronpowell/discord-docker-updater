using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordDockerUpdater.Configuration;
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
    // Agent mode — just the Docker services, no Discord
    builder.Services.AddSingleton<ContainerInspector>();
    builder.Services.AddSingleton<DockerComposeExecutor>();
    builder.Services.AddSingleton<ComposeFileUpdater>();
}
else
{
    // Bot mode — full Discord + Docker services
    builder.Services.AddSingleton<UpdateTracker>();
    builder.Services.AddSingleton<ContainerInspector>();
    builder.Services.AddSingleton<DiscordNotificationService>();
    builder.Services.AddSingleton<DockerComposeExecutor>();
    builder.Services.AddSingleton<ComposeFileUpdater>();
    builder.Services.AddHttpClient<AgentClient>();

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
    return Results.Ok(new
    {
        status = "healthy",
        mode = "bot",
        discord = client.ConnectionState == ConnectionState.Connected ? "connected" : "disconnected",
        pendingUpdates = tracker.GetPendingCount()
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

// Agent update endpoint — receives commands from the central bot
app.MapPost("/agent/update", async (HttpContext httpContext, AgentUpdateRequest request,
    ContainerInspector inspector, DockerComposeExecutor executor, ComposeFileUpdater updater,
    IOptions<BotConfiguration> botCfg, ILogger<Program> agentLogger) =>
{
    // Validate agent token
    var agentToken = botCfg.Value.AgentToken;
    if (!string.IsNullOrWhiteSpace(agentToken))
    {
        var authHeader = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) ||
            !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            authHeader["Bearer ".Length..] != agentToken)
        {
            agentLogger.LogWarning("Agent update request rejected: invalid or missing authorization token");
            return Results.Unauthorized();
        }
    }

    if (string.IsNullOrWhiteSpace(request.ContainerName))
    {
        return Results.BadRequest(new { error = "ContainerName is required" });
    }

    agentLogger.LogInformation("Agent received update command for container {Container}", request.ContainerName);

    // Inspect container to find compose info
    var composeInfo = await inspector.InspectAsync(request.ContainerName);
    if (composeInfo == null)
    {
        return Results.Ok(new AgentUpdateResponse
        {
            Success = false,
            ErrorOutput = $"Could not find compose info for container '{request.ContainerName}'"
        });
    }

    // Execute the update
    try
    {
        var result = await executor.UpdateServiceAsync(composeInfo.ConfigFile, composeInfo.ServiceName);

        var sourceUpdated = false;
        if (result.Success && !string.IsNullOrWhiteSpace(request.Digest) && !string.IsNullOrWhiteSpace(request.ImageName))
        {
            var pinnedImage = ComposeFileUpdater.BuildImageWithDigest(request.ImageName, request.Digest);
            sourceUpdated = await updater.UpdateImageReferenceAsync(
                composeInfo.ConfigFile, composeInfo.ServiceName, pinnedImage);
        }

        return Results.Ok(new AgentUpdateResponse
        {
            Success = result.Success,
            PullOutput = result.PullOutput,
            UpOutput = result.UpOutput,
            ErrorOutput = result.ErrorOutput,
            DurationSeconds = result.Duration.TotalSeconds,
            ServiceName = composeInfo.ServiceName,
            ProjectName = composeInfo.ProjectName,
            ConfigFile = composeInfo.ConfigFile,
            SourceUpdated = sourceUpdated
        });
    }
    catch (Exception ex)
    {
        agentLogger.LogError(ex, "Agent update failed for container {Container}", request.ContainerName);
        return Results.Ok(new AgentUpdateResponse
        {
            Success = false,
            ErrorOutput = $"Exception: {ex.Message}"
        });
    }
})
.WithName("AgentUpdate");

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

        if (string.IsNullOrWhiteSpace(config.AgentToken))
        {
            logger.LogWarning(
                "Agent token is not configured (Bot:AgentToken). " +
                "The /agent/update endpoint is open to unauthenticated requests. " +
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
