using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordDockerUpdater.Configuration;
using DiscordDockerUpdater.Models;
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
    ComposeFileUpdater composeFileUpdater,
    DiscordNotificationService notificationService,
    AgentClient agentClient) : IHostedService
{
    private readonly BotConfiguration _config = config.Value;
    private readonly HashSet<ulong> _allowedUserIds = ParseUserIdSet(config.Value.AllowedUserIds);

    private static HashSet<ulong> ParseUserIdSet(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new HashSet<ulong>();
        return raw
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => ulong.TryParse(s, out var id) ? id : 0UL)
            .Where(id => id != 0)
            .ToHashSet();
    }

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

        // Hide multi-host agent commands when the feature is disabled.
        // The corresponding SignalR /agent-hub endpoint isn't mapped either
        // (see Program.cs), so the whole feature surface is gone — not just
        // the slash command picker entries.
        if (!_config.MultiHostMode)
        {
            await interactionService.RemoveModuleAsync<DiscordDockerUpdater.Modules.AgentModule>();
            logger.LogInformation(
                "Multi-host mode disabled — /agents and /agent-info commands not registered");
        }

        // Register slash commands to a single guild when GuildId is set; otherwise global.
        if (_config.GuildId != 0)
        {
            await interactionService.RegisterCommandsToGuildAsync(_config.GuildId);
            logger.LogInformation("Slash commands registered to guild {GuildId}", _config.GuildId);
        }
        else
        {
            await interactionService.RegisterCommandsGloballyAsync();
            logger.LogWarning(
                "Slash commands registered GLOBALLY — set Bot:GuildId to restrict to one guild.");
        }

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

        if (!IsAuthorized(interaction))
        {
            await RespondUnauthorizedAsync(interaction);
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
    /// Defers and disables buttons immediately, then offloads long-running work
    /// to a background task so the gateway thread is never blocked.
    /// </summary>
    private async Task HandleButtonAsync(SocketMessageComponent component)
    {
        try
        {
            if (!IsAuthorized(component))
            {
                await RespondUnauthorizedAsync(component);
                return;
            }

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
    /// Immediately defers the interaction and disables buttons to prevent duplicate clicks,
    /// then offloads the Docker update to a background task to avoid blocking the gateway.
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

        // Disable buttons immediately so the user can't click again
        var inProgressComponents = new ComponentBuilder()
            .WithButton("⏳ Updating...", customId: "disabled_update", style: ButtonStyle.Primary, disabled: true)
            .WithButton("❌ Dismiss", customId: "disabled_dismiss", style: ButtonStyle.Secondary, disabled: true)
            .Build();

        var inProgressEmbed = new EmbedBuilder()
            .WithTitle("⏳ Update In Progress")
            .WithDescription($"Updating **{containerName}** — this may take a few minutes...")
            .WithColor(0xFFA500) // Orange
            .AddField("Image", imageName, inline: false)
            .AddField("Triggered By", component.User.Mention, inline: true)
            .WithTimestamp(DateTimeOffset.UtcNow)
            .WithFooter($"Update ID: {update.Id}")
            .Build();

        try
        {
            await component.ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = inProgressEmbed;
                msg.Components = inProgressComponents;
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update message to in-progress state for {UpdateId}", update.Id);
        }

        // Offload the long-running Docker work to a background task so we don't block the gateway
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteUpdateAsync(component, update, imageName, containerName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background update task failed for {UpdateId}", update.Id);
            }
        });
    }

    /// <summary>
    /// Executes the actual Docker update work on a background thread.
    /// </summary>
    private async Task ExecuteUpdateAsync(
        SocketMessageComponent component, PendingUpdate update,
        string imageName, string containerName)
    {
        // Resolve the compose project via docker inspect
        var composeInfo = await containerInspector.InspectAsync(containerName);

        // Check if this update should be routed to a remote agent
        var hostname = update.Payload.Hostname;
        if (agentClient.IsAgentConnected(hostname))
        {
            await ExecuteRemoteUpdateAsync(component, update, imageName, containerName);
            return;
        }

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

            await RestoreActionableButtonsAsync(component, update);
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

            await RestoreActionableButtonsAsync(component, update);
            await component.FollowupAsync(embed: exceptionEmbed);
            return;
        }

        // Handle result
        if (result.Success)
        {
            // Mark as completed in tracker
            updateTracker.MarkCompleted(update.Id);

            // Update compose file source if enabled
            var sourceUpdated = false;
            if (!string.IsNullOrWhiteSpace(update.Payload.Digest))
            {
                var pinnedImage = ComposeFileUpdater.BuildImageWithDigest(imageName, update.Payload.Digest);
                sourceUpdated = await composeFileUpdater.UpdateImageReferenceAsync(
                    composeInfo.ConfigFile, serviceName, pinnedImage);
            }

            // Build success embed for the original message
            var successEmbed = new EmbedBuilder()
                .WithTitle("✅ Updated Successfully")
                .WithDescription($"Container **{containerName}** has been updated")
                .WithColor(0x00FF00) // Green
                .AddField("Image", imageName, inline: false)
                .AddField("Host", update.Payload.Hostname ?? "local", inline: true)
                .AddField("Service", serviceName, inline: true)
                .AddField("Project", composeInfo.ProjectName, inline: true)
                .AddField("Triggered By", component.User.Mention, inline: true)
                .AddField("Duration", $"{result.Duration.TotalSeconds:F2}s", inline: true);

            if (sourceUpdated)
            {
                successEmbed.AddField("Source Updated", $"✅ `{Path.GetFileName(composeInfo.ConfigFile)}`", inline: true);
            }

            var builtSuccessEmbed = successEmbed
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
                    msg.Embed = builtSuccessEmbed;
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
                .AddField("Host", update.Payload.Hostname ?? "local", inline: true)
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

            await RestoreActionableButtonsAsync(component, update);
            await component.FollowupAsync(embeds: new[] { failureEmbed, errorDetailsEmbed });

            logger.LogError(
                "Update failed for {UpdateId}. Error: {Error}",
                update.Id,
                result.ErrorOutput);
        }
    }

    /// <summary>
    /// Executes an update via a remote agent when the container is on a different host.
    /// </summary>
    private async Task ExecuteRemoteUpdateAsync(
        SocketMessageComponent component, PendingUpdate update,
        string imageName, string containerName)
    {
        var hostname = update.Payload.Hostname ?? "unknown";
        logger.LogInformation(
            "Routing update {UpdateId} for container {Container} to remote agent (host: {Hostname})",
            update.Id, containerName, hostname);

        try
        {
            var request = new AgentUpdateRequest
            {
                ContainerName = containerName,
                ImageName = imageName,
                Digest = update.Payload.Digest,
                UpdateId = update.Id
            };

            var response = await agentClient.SendUpdateAsync(hostname, request);

            if (response.Success)
            {
                updateTracker.MarkCompleted(update.Id);

                var successEmbed = new EmbedBuilder()
                    .WithTitle("✅ Updated Successfully")
                    .WithDescription($"Container **{containerName}** has been updated")
                    .WithColor(0x00FF00)
                    .AddField("Image", imageName, inline: false)
                    .AddField("Host", hostname, inline: true)
                    .AddField("Service", response.ServiceName ?? "N/A", inline: true)
                    .AddField("Project", response.ProjectName ?? "N/A", inline: true)
                    .AddField("Triggered By", component.User.Mention, inline: true)
                    .AddField("Duration", $"{response.DurationSeconds:F2}s", inline: true);

                if (response.SourceUpdated)
                {
                    successEmbed.AddField("Source Updated", $"✅ `{Path.GetFileName(response.ConfigFile ?? "")}`", inline: true);
                }

                var builtSuccessEmbed = successEmbed
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .WithFooter($"Update ID: {update.Id}")
                    .Build();

                var disabledComponents = new ComponentBuilder()
                    .WithButton("🔄 Update", customId: "disabled_update", style: ButtonStyle.Success, disabled: true)
                    .WithButton("❌ Dismiss", customId: "disabled_dismiss", style: ButtonStyle.Secondary, disabled: true)
                    .Build();

                await component.ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = builtSuccessEmbed;
                    msg.Components = disabledComponents;
                });

                // Follow up with details
                var detailsEmbed = new EmbedBuilder()
                    .WithTitle("📋 Update Details")
                    .WithColor(0x00FF00)
                    .WithTimestamp(DateTimeOffset.UtcNow);

                if (!string.IsNullOrWhiteSpace(response.PullOutput))
                    detailsEmbed.AddField("Pull Output", TruncateForDiscord(response.PullOutput, 1024), inline: false);
                if (!string.IsNullOrWhiteSpace(response.UpOutput))
                    detailsEmbed.AddField("Up Output", TruncateForDiscord(response.UpOutput, 1024), inline: false);

                await component.FollowupAsync(embed: detailsEmbed.Build());
            }
            else
            {
                var failureEmbed = new EmbedBuilder()
                    .WithTitle("❌ Update Failed")
                    .WithDescription($"Failed to update **{imageName}** on host **{hostname}**")
                    .WithColor(0xFF0000)
                    .AddField("Container", containerName, inline: true)
                    .AddField("Host", hostname, inline: true)
                    .AddField("Duration", $"{response.DurationSeconds:F2}s", inline: true)
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .WithFooter($"Update ID: {update.Id}")
                    .Build();

                var errorEmbed = new EmbedBuilder()
                    .WithTitle("❌ Error Output")
                    .WithColor(0xFF0000)
                    .AddField("Error", TruncateForDiscord(response.ErrorOutput ?? "Unknown error", 1024), inline: false)
                    .WithTimestamp(DateTimeOffset.UtcNow)
                    .Build();

                await RestoreActionableButtonsAsync(component, update);
                await component.FollowupAsync(embeds: new[] { failureEmbed, errorEmbed });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to route update to remote agent for host {Hostname}", hostname);

            var errorEmbed = new EmbedBuilder()
                .WithTitle("❌ Agent Unreachable")
                .WithDescription($"Could not reach agent on host **{hostname}**")
                .WithColor(0xFF0000)
                .AddField("Container", containerName, inline: true)
                .AddField("Error", TruncateForDiscord(ex.Message, 1024), inline: false)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .WithFooter($"Update ID: {update.Id}")
                .Build();

            await RestoreActionableButtonsAsync(component, update);
            await component.FollowupAsync(embed: errorEmbed);
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
    /// On failure, restores the original message's "Update" / "Dismiss" buttons (with the
    /// real <c>update:&lt;id&gt;</c> / <c>dismiss:&lt;id&gt;</c> custom IDs) so the user can
    /// retry or clear the notification. Without this, the mid-flight "⏳ Updating..." state
    /// (set in HandleUpdateButtonAsync) is left in place forever — both buttons stay disabled
    /// and the message is unrecoverable.
    /// </summary>
    private async Task RestoreActionableButtonsAsync(
        SocketMessageComponent component, PendingUpdate update)
    {
        var imageName = update.Payload.Image ?? "unknown";
        var containerName = update.Payload.Metadata?.CtnNames?.TrimStart('/') ?? "unknown";

        var failedEmbed = new EmbedBuilder()
            .WithTitle("⚠️ Update Failed — Click Update to retry, or Dismiss to clear")
            .WithDescription($"The previous update attempt for **{containerName}** failed. See the error details below.")
            .WithColor(0xFFA500) // Orange — actionable, not terminal
            .AddField("Image", imageName, inline: false)
            .AddField("Triggered By", component.User.Mention, inline: true)
            .WithTimestamp(DateTimeOffset.UtcNow)
            .WithFooter($"Update ID: {update.Id}")
            .Build();

        var actionableComponents = new ComponentBuilder()
            .WithButton(label: "🔄 Update", customId: $"update:{update.Id}", style: ButtonStyle.Primary)
            .WithButton(label: "❌ Dismiss", customId: $"dismiss:{update.Id}", style: ButtonStyle.Secondary)
            .Build();

        try
        {
            await component.ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = failedEmbed;
                msg.Components = actionableComponents;
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restore actionable buttons after update failure for {UpdateId}", update.Id);
        }
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

    /// <summary>
    /// Gates interactions on four checks: DM context, user(s), channel, guild.
    /// Each ID-based check is enforced only when its config value is non-empty/
    /// non-zero, so the bot still works in unrestricted mode if no IDs are set
    /// (with loud startup warnings). DMs are always rejected when a GuildId is
    /// configured, regardless of who sent them — slash commands are guild-scoped
    /// and the bot never posts buttons outside the configured channel, but this
    /// is defense-in-depth with a clearer log line than the generic guild check.
    /// Multiple allowed users can be configured via a comma- or semicolon-
    /// separated string in Bot:AllowedUserIds.
    /// </summary>
    private bool IsAuthorized(SocketInteraction interaction)
    {
        if (_config.GuildId != 0 && interaction.GuildId is null)
        {
            logger.LogWarning(
                "Rejected DM interaction from user {UserId} ({Username})",
                interaction.User.Id, interaction.User.Username);
            return false;
        }
        if (_allowedUserIds.Count > 0 && !_allowedUserIds.Contains(interaction.User.Id))
        {
            logger.LogWarning(
                "Rejected interaction from unauthorized user {UserId} ({Username}) in channel {ChannelId}",
                interaction.User.Id, interaction.User.Username, interaction.ChannelId);
            return false;
        }
        if (_config.ChannelId != 0 && interaction.ChannelId != _config.ChannelId)
        {
            logger.LogWarning(
                "Rejected interaction from wrong channel {ChannelId} (user {UserId})",
                interaction.ChannelId, interaction.User.Id);
            return false;
        }
        if (_config.GuildId != 0 && interaction.GuildId != _config.GuildId)
        {
            logger.LogWarning(
                "Rejected interaction from wrong guild {GuildId} (user {UserId})",
                interaction.GuildId, interaction.User.Id);
            return false;
        }
        return true;
    }

    private async Task RespondUnauthorizedAsync(SocketInteraction interaction)
    {
        try
        {
            if (!interaction.HasResponded)
                await interaction.RespondAsync("Not authorized.", ephemeral: true);
        }
        catch (Discord.Net.HttpException ex)
        {
            // Interaction token expired, rate-limited, or already-acked — best-effort.
            logger.LogDebug(ex, "Could not send unauthorized ack to interaction {Id}", interaction.Id);
        }
        catch (TimeoutException ex)
        {
            logger.LogDebug(ex, "Timed out sending unauthorized ack to interaction {Id}", interaction.Id);
        }
    }
}
