using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordDockerUpdater.Configuration;
using Microsoft.Extensions.Options;

namespace DiscordDockerUpdater.Services;

public class DiscordBotService(
    DiscordSocketClient client,
    InteractionService interactionService,
    IServiceProvider serviceProvider,
    IOptions<BotConfiguration> config,
    ILogger<DiscordBotService> logger,
    UpdateTracker updateTracker,
    ContainerInspector containerInspector,
    DockerComposeExecutor composeExecutor,
    DiscordNotificationService notificationService) : IHostedService
{
    private readonly BotConfiguration _config = config.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Wire up Discord.Net logging to ILogger
        client.Log += LogAsync;
        interactionService.Log += LogAsync;

        // Set up Ready handler to register slash commands
        client.Ready += OnReadyAsync;

        // Set up interaction handler
        client.InteractionCreated += HandleInteractionAsync;

        // Set up button interaction handler
        client.ButtonExecuted += HandleButtonAsync;

        // Log in and start the client
        await client.LoginAsync(TokenType.Bot, _config.DiscordToken);
        await client.StartAsync();

        logger.LogInformation("Discord bot service started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Discord bot service stopping");

        await client.LogoutAsync();
        await client.StopAsync();
    }

    private async Task OnReadyAsync()
    {
        logger.LogInformation("Discord bot is ready. Connected as {Username}", client.CurrentUser.Username);

        // Signal that the client is fully ready (guild/channel cache populated)
        notificationService.SignalReady();

        // Discover and add interaction modules
        await interactionService.AddModulesAsync(Assembly.GetEntryAssembly(), serviceProvider);

        // Register commands globally
        await interactionService.RegisterCommandsGloballyAsync();

        logger.LogInformation("Slash commands registered globally");

        // Post a startup message to the configured channel
        if (_config.ChannelId != 0)
        {
            try
            {
                if (client.GetChannel(_config.ChannelId) is IMessageChannel channel)
                {
                    var embedBuilder = new EmbedBuilder()
                        .WithTitle("🟢 Bot Online")
                        .WithDescription($"**{client.CurrentUser.Username}** is ready and listening for Docker image updates.")
                        .WithColor(0x00FF00)
                        .WithTimestamp(DateTimeOffset.UtcNow);

                    // Add logo thumbnail if configured
                    if (!string.IsNullOrWhiteSpace(_config.LogoUrl))
                    {
                        embedBuilder.WithThumbnailUrl(_config.LogoUrl);
                    }

                    var embed = embedBuilder.Build();

                    await channel.SendMessageAsync(embed: embed);
                    logger.LogInformation("Startup message posted to channel {ChannelId}", _config.ChannelId);
                }
                else
                {
                    logger.LogWarning("Could not find channel {ChannelId} to post startup message", _config.ChannelId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to post startup message to channel {ChannelId}", _config.ChannelId);
            }
        }
    }

    private async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        // Skip component interactions (buttons) — they are handled by ButtonExecuted
        if (interaction is SocketMessageComponent)
        {
            return;
        }

        try
        {
            // Create an execution context
            var context = new SocketInteractionContext(client, interaction);

            // Execute the command
            var result = await interactionService.ExecuteCommandAsync(context, serviceProvider);

            if (!result.IsSuccess)
            {
                logger.LogError("Error executing interaction: {Error}", result.ErrorReason);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception handling interaction");

            // If the interaction is a slash command, respond with an error message
            if (interaction.Type == InteractionType.ApplicationCommand)
            {
                var response = interaction.HasResponded
                    ? interaction.FollowupAsync("An error occurred while processing the command.", ephemeral: true)
                    : interaction.RespondAsync("An error occurred while processing the command.", ephemeral: true);

                await response;
            }
        }
    }

    /// <summary>
    /// Handles button interactions from Discord message components.
    /// Implements async/await pattern for responsive user experience.
    /// </summary>
    private async Task HandleButtonAsync(SocketMessageComponent component)
    {
        try
        {
            // Parse the custom ID (format: "action:updateId")
            var parts = component.Data.CustomId.Split(':', 2);
            if (parts.Length != 2)
            {
                logger.LogWarning("Invalid button custom ID format: {CustomId}", component.Data.CustomId);
                await component.RespondAsync("Invalid button action.", ephemeral: true);
                return;
            }

            var action = parts[0];
            var updateId = parts[1];

            logger.LogInformation(
                "Button interaction received: action={Action}, updateId={UpdateId}, user={User}",
                action,
                updateId,
                component.User.Username);

            // Retrieve the update from the tracker
            var update = updateTracker.GetUpdate(updateId);
            if (update == null)
            {
                logger.LogWarning("Update {UpdateId} not found in tracker", updateId);
                await component.RespondAsync("Update not found. It may have already been processed.", ephemeral: true);
                return;
            }

            // Handle the action
            switch (action.ToLowerInvariant())
            {
                case "update":
                    await HandleUpdateButtonAsync(component, update);
                    break;

                case "dismiss":
                    await HandleDismissButtonAsync(component, update);
                    break;

                default:
                    logger.LogWarning("Unknown button action: {Action}", action);
                    await component.RespondAsync("Unknown action.", ephemeral: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception handling button interaction");

            // Try to respond with an error if we haven't responded yet
            try
            {
                if (!component.HasResponded)
                {
                    await component.RespondAsync("An error occurred while processing the button action.", ephemeral: true);
                }
            }
            catch
            {
                // Ignore - we tried our best
            }
        }
    }

    /// <summary>
    /// Handles the "Update" button press.
    /// Executes docker compose pull and up commands, updating the Discord message with results.
    /// </summary>
    private async Task HandleUpdateButtonAsync(SocketMessageComponent component, PendingUpdate update)
    {
        // Check if already completed to prevent duplicate processing
        if (update.IsCompleted)
        {
            logger.LogInformation(
                "Update {UpdateId} has already been processed, ignoring duplicate button press by {User}",
                update.Id,
                component.User.Username);

            await component.RespondAsync(
                "⚠️ This update has already been processed.",
                ephemeral: true);
            return;
        }

        // Acknowledge the interaction immediately (prevents "Interaction failed" message)
        await component.DeferAsync(ephemeral: false);

        var imageName = update.Payload.Image ?? "unknown";
        var containerName = update.Payload.Metadata?.CtnNames?.TrimStart('/') ?? "unknown";

        logger.LogInformation(
            "Update button pressed for {UpdateId} (image: {Image}, container: {Container}) by user {User}",
            update.Id,
            imageName,
            containerName,
            component.User.Username);

        // Resolve the compose project via docker inspect
        var composeInfo = await containerInspector.InspectAsync(containerName);

        if (composeInfo == null)
        {
            logger.LogWarning(
                "Could not find compose info for container '{ContainerName}'",
                containerName);

            var errorEmbed = new EmbedBuilder()
                .WithTitle("❌ Update Failed")
                .WithDescription($"Could not find Docker Compose info for container **{containerName}**. Is it managed by Compose?")
                .WithColor(0xFF0000) // Red
                .AddField("Container", containerName, inline: true)
                .AddField("Image", imageName, inline: true)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .WithFooter($"Update ID: {update.Id}")
                .Build();

            await component.FollowupAsync(embed: errorEmbed);
            return;
        }

        var serviceName = composeInfo.ServiceName;

        logger.LogInformation(
            "Resolved container '{ContainerName}' to compose project '{ProjectName}', service '{ServiceName}'",
            containerName,
            composeInfo.ProjectName,
            serviceName);

        // Execute the update
        ComposeExecutionResult result;
        try
        {
            result = await composeExecutor.UpdateServiceAsync(
                composeInfo.ConfigFile,
                serviceName,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during compose execution for service '{ServiceName}'", serviceName);

            var exceptionEmbed = new EmbedBuilder()
                .WithTitle("❌ Update Failed")
                .WithDescription($"An exception occurred while updating **{imageName}**")
                .WithColor(0xFF0000) // Red
                .AddField("Container", containerName, inline: true)
                .AddField("Service", serviceName, inline: true)
                .AddField("Error", TruncateForDiscord(ex.Message, 1024), inline: false)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .WithFooter($"Update ID: {update.Id}")
                .Build();

            await component.FollowupAsync(embed: exceptionEmbed);
            return;
        }

        // Handle result
        if (result.Success)
        {
            // Mark as completed in tracker
            updateTracker.MarkCompleted(update.Id);

            // Build success embed for the original message
            var successEmbed = new EmbedBuilder()
                .WithTitle("✅ Updated Successfully")
                .WithDescription($"Container **{containerName}** has been updated")
                .WithColor(0x00FF00) // Green
                .AddField("Image", imageName, inline: false)
                .AddField("Service", serviceName, inline: true)
                .AddField("Project", composeInfo.ProjectName, inline: true)
                .AddField("Triggered By", component.User.Mention, inline: true)
                .AddField("Duration", $"{result.Duration.TotalSeconds:F2}s", inline: true)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .WithFooter($"Update ID: {update.Id}")
                .Build();

            // Disable the buttons
            var disabledComponents = new ComponentBuilder()
                .WithButton("🔄 Update", customId: "disabled_update", style: ButtonStyle.Success, disabled: true)
                .WithButton("❌ Dismiss", customId: "disabled_dismiss", style: ButtonStyle.Secondary, disabled: true)
                .Build();

            // Update the original message
            try
            {
                await component.ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = successEmbed;
                    msg.Components = disabledComponents;
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update original message for successful update {UpdateId}", update.Id);
            }

            // Follow up with detailed output
            var detailsEmbedBuilder = new EmbedBuilder()
                .WithTitle("📋 Update Details")
                .WithColor(0x00FF00) // Green
                .WithTimestamp(DateTimeOffset.UtcNow);

            if (!string.IsNullOrWhiteSpace(result.PullOutput))
            {
                detailsEmbedBuilder.AddField(
                    "Pull Output",
                    TruncateForDiscord(result.PullOutput, 1024),
                    inline: false);
            }

            if (!string.IsNullOrWhiteSpace(result.UpOutput))
            {
                detailsEmbedBuilder.AddField(
                    "Up Output",
                    TruncateForDiscord(result.UpOutput, 1024),
                    inline: false);
            }

            await component.FollowupAsync(embed: detailsEmbedBuilder.Build());

            logger.LogInformation(
                "Update completed successfully for {UpdateId} in {Duration:F2}s",
                update.Id,
                result.Duration.TotalSeconds);
        }
        else
        {
            // Failure - build error embed
            var failureEmbed = new EmbedBuilder()
                .WithTitle("❌ Update Failed")
                .WithDescription($"Failed to update **{imageName}**")
                .WithColor(0xFF0000) // Red
                .AddField("Container", containerName, inline: true)
                .AddField("Service", serviceName, inline: true)
                .AddField("Project", composeInfo.ProjectName, inline: true)
                .AddField("Duration", $"{result.Duration.TotalSeconds:F2}s", inline: true)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .WithFooter($"Update ID: {update.Id}")
                .Build();

            // Show error output
            var errorDetailsEmbed = new EmbedBuilder()
                .WithTitle("❌ Error Output")
                .WithColor(0xFF0000) // Red
                .AddField("Error", TruncateForDiscord(result.ErrorOutput, 1024), inline: false)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            await component.FollowupAsync(embeds: new[] { failureEmbed, errorDetailsEmbed });

            logger.LogError(
                "Update failed for {UpdateId}. Error: {Error}",
                update.Id,
                result.ErrorOutput);
        }
    }

    /// <summary>
    /// Truncates text to fit within Discord's field value limits.
    /// Implements clean truncation with ellipsis indicator.
    /// </summary>
    private static string TruncateForDiscord(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Handles the "Dismiss" button press.
    /// Marks the update as completed and updates the Discord message to reflect dismissal.
    /// </summary>
    private async Task HandleDismissButtonAsync(SocketMessageComponent component, PendingUpdate update)
    {
        // Mark as completed in the tracker
        updateTracker.MarkCompleted(update.Id);

        logger.LogInformation(
            "Update {UpdateId} dismissed by user {User}",
            update.Id,
            component.User.Username);

        // Build updated embed showing dismissal
        var dismissedEmbedBuilder = new EmbedBuilder()
            .WithTitle("🐳 Image Update Available")
            .WithDescription($"✅ **Dismissed by {component.User.Mention}**")
            .WithColor(0x808080) // Gray
            .WithTimestamp(update.ReceivedAt)
            .WithFooter($"Update ID: {update.Id}");

        // Add original update info as fields
        var payload = update.Payload;
        if (!string.IsNullOrWhiteSpace(payload.Image))
        {
            dismissedEmbedBuilder.AddField("Image", payload.Image, inline: false);
        }

        var dismissedEmbed = dismissedEmbedBuilder.Build();

        // Disable the buttons by creating new disabled buttons
        var disabledComponents = new ComponentBuilder()
            .WithButton("🔄 Update", customId: "disabled_update", style: ButtonStyle.Primary, disabled: true)
            .WithButton("❌ Dismiss", customId: "disabled_dismiss", style: ButtonStyle.Secondary, disabled: true)
            .Build();

        try
        {
            // Update the original message
            await component.UpdateAsync(msg =>
            {
                msg.Embed = dismissedEmbed;
                msg.Components = disabledComponents;
            });

            logger.LogInformation("Updated Discord message for dismissed update {UpdateId}", update.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update Discord message for dismissed update {UpdateId}", update.Id);

            // Fallback: respond with ephemeral message
            await component.RespondAsync($"Update dismissed by {component.User.Mention}", ephemeral: true);
        }
    }

    private Task LogAsync(LogMessage message)
    {
        var logLevel = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information
        };

        logger.Log(logLevel, message.Exception, "[Discord.Net] {Message}", message.Message);

        return Task.CompletedTask;
    }
}
