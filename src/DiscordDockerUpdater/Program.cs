using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordDockerUpdater.Configuration;
using DiscordDockerUpdater.Models;
using DiscordDockerUpdater.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);

// Configure strongly-typed settings
builder.Services.Configure<BotConfiguration>(
    builder.Configuration.GetSection(BotConfiguration.SectionName));

// Register services
builder.Services.AddSingleton<UpdateTracker>();
builder.Services.AddSingleton<ContainerInspector>();
builder.Services.AddSingleton<DiscordNotificationService>();
builder.Services.AddSingleton<DockerComposeExecutor>();

// Register Discord services
builder.Services.AddSingleton(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages });
builder.Services.AddSingleton<DiscordSocketClient>();
builder.Services.AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>()));
builder.Services.AddHostedService<DiscordBotService>();
builder.Services.AddHostedService<StaleUpdateCleanupService>();

var app = builder.Build();

// Validate configuration on startup
ValidateConfiguration(app.Services, app.Logger);

// Health check endpoint
app.MapGet("/health", (UpdateTracker tracker, DiscordSocketClient client) =>
{
    var status = new
    {
        status = "healthy",
        discord = client.ConnectionState == ConnectionState.Connected ? "connected" : "disconnected",
        pendingUpdates = tracker.GetPendingCount()
    };

    return Results.Ok(status);
})
.WithName("HealthCheck");

// Webhook endpoint for receiving Diun notifications
app.MapPost("/webhook/diun", async (DiunPayload payload, UpdateTracker tracker,
    DiscordNotificationService notifier, ILogger<Program> logger) =>
{
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

app.Run();

/// <summary>
/// Validates configuration on startup and logs warnings/errors for common misconfigurations.
/// Following the fail-fast principle to catch configuration issues early.
/// </summary>
static void ValidateConfiguration(IServiceProvider services, ILogger logger)
{
    var config = services.GetRequiredService<IOptions<BotConfiguration>>().Value;

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

    logger.LogInformation("Configuration validation completed successfully");
}
