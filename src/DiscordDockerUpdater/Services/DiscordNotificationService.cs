using Discord;
using Discord.WebSocket;
using DiscordDockerUpdater.Configuration;
using Microsoft.Extensions.Options;

namespace DiscordDockerUpdater.Services;

/// <summary>
/// Service responsible for posting interactive Discord notifications for Docker image updates.
/// Follows the Single Responsibility Principle by focusing solely on Discord communication.
/// </summary>
public class DiscordNotificationService(
    DiscordSocketClient client,
    IOptions<BotConfiguration> config,
    ILogger<DiscordNotificationService> logger)
{
    private readonly BotConfiguration _config = config.Value;

    /// <summary>
    /// Posts an interactive embed to the configured Discord channel for a pending update.
    /// The embed shows: image name, current tag, status, platform, container name, provider.
    /// Includes a button row with "Update" and "Dismiss" actions.
    /// </summary>
    /// <param name="update">The pending update to notify about</param>
    /// <exception cref="InvalidOperationException">Thrown if the Discord client is not ready or channel is not found</exception>
    public async Task NotifyUpdateAvailableAsync(PendingUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        // Ensure Discord client is ready
        if (client.ConnectionState != ConnectionState.Connected)
        {
            logger.LogWarning(
                "Discord client not connected (state: {State}). Cannot send notification for update {UpdateId}",
                client.ConnectionState,
                update.Id);
            throw new InvalidOperationException($"Discord client is not connected (current state: {client.ConnectionState})");
        }

        // Get the configured channel
        var channel = client.GetChannel(_config.ChannelId) as IMessageChannel;
        if (channel == null)
        {
            logger.LogError(
                "Could not find channel with ID {ChannelId}. Cannot send notification for update {UpdateId}",
                _config.ChannelId,
                update.Id);
            throw new InvalidOperationException($"Channel with ID {_config.ChannelId} not found");
        }

        // Build the embed
        var embed = BuildUpdateEmbed(update);

        // Build the component (button) row
        var components = BuildUpdateComponents(update.Id);

        try
        {
            // Send the message
            var message = await channel.SendMessageAsync(embed: embed, components: components);

            // Update the tracker with the Discord message ID
            update.DiscordMessageId = message.Id;

            logger.LogInformation(
                "Posted Discord notification for update {UpdateId} (image: {Image}) in channel {ChannelId}. Message ID: {MessageId}",
                update.Id,
                update.Payload.Image,
                _config.ChannelId,
                message.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to send Discord message for update {UpdateId}",
                update.Id);
            throw;
        }
    }

    /// <summary>
    /// Builds the Discord embed showing update information.
    /// Uses clean, professional formatting with relevant fields.
    /// </summary>
    private Embed BuildUpdateEmbed(PendingUpdate update)
    {
        var payload = update.Payload;
        var builder = new EmbedBuilder()
            .WithTitle("🐳 Image Update Available")
            .WithColor(0xFFA500) // Orange
            .WithTimestamp(update.ReceivedAt)
            .WithFooter($"Update ID: {update.Id}");

        // Add logo thumbnail if configured
        if (!string.IsNullOrWhiteSpace(_config.LogoUrl))
        {
            builder.WithThumbnailUrl(_config.LogoUrl);
        }

        // Add key fields from the payload
        if (!string.IsNullOrWhiteSpace(payload.Image))
        {
            builder.AddField("Image", payload.Image, inline: false);
        }

        if (!string.IsNullOrWhiteSpace(payload.Status))
        {
            builder.AddField("Status", payload.Status, inline: true);
        }

        if (!string.IsNullOrWhiteSpace(payload.Platform))
        {
            builder.AddField("Platform", payload.Platform, inline: true);
        }

        // Extract container name from metadata if available
        var containerName = payload.Metadata?.CtnNames;
        if (!string.IsNullOrWhiteSpace(containerName))
        {
            // Clean up container name (diun often includes leading slash)
            containerName = containerName.TrimStart('/');
            builder.AddField("Container", containerName, inline: true);
        }

        if (!string.IsNullOrWhiteSpace(payload.Provider))
        {
            builder.AddField("Provider", payload.Provider, inline: true);
        }

        return builder.Build();
    }

    /// <summary>
    /// Builds the interactive component (button) row for the update message.
    /// Includes "Update" and "Dismiss" buttons with custom IDs for handling.
    /// </summary>
    private MessageComponent BuildUpdateComponents(string updateId)
    {
        var builder = new ComponentBuilder()
            .WithButton(
                label: "🔄 Update",
                customId: $"update:{updateId}",
                style: ButtonStyle.Primary)
            .WithButton(
                label: "❌ Dismiss",
                customId: $"dismiss:{updateId}",
                style: ButtonStyle.Secondary);

        return builder.Build();
    }
}
