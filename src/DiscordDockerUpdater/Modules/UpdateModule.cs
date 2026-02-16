using Discord;
using Discord.Interactions;
using DiscordDockerUpdater.Services;

namespace DiscordDockerUpdater.Modules;

public class UpdateModule(
    ILogger<UpdateModule> logger,
    UpdateTracker updateTracker,
    ContainerInspector containerInspector,
    DockerComposeExecutor composeExecutor,
    ComposeFileUpdater composeFileUpdater) : InteractionModuleBase<SocketInteractionContext>
{

    [SlashCommand("list-updates", "Lists all pending container updates")]
    public async Task ListUpdatesAsync()
    {
        logger.LogInformation("list-updates command invoked by {User}", Context.User.Username);

        var pendingUpdates = updateTracker.GetPendingUpdates().ToList();

        if (pendingUpdates.Count == 0)
        {
            await RespondAsync("No pending updates.", ephemeral: true);
            return;
        }

        // Build an embed showing all pending updates
        var embedBuilder = new EmbedBuilder()
            .WithTitle("📋 Pending Container Updates")
            .WithColor(0x0099FF) // Blue
            .WithTimestamp(DateTimeOffset.UtcNow)
            .WithFooter($"{pendingUpdates.Count} pending update(s)");

        foreach (var update in pendingUpdates.Take(10)) // Limit to 10 to avoid hitting embed limits
        {
            var payload = update.Payload;
            var imageName = payload.Image ?? "Unknown";
            var containerName = payload.Metadata?.CtnNames?.TrimStart('/') ?? "N/A";
            var status = payload.Status ?? "N/A";
            var receivedAgo = GetRelativeTime(update.ReceivedAt);

            embedBuilder.AddField(
                name: $"🐳 {imageName}",
                value: $"**Container:** {containerName}\n**Status:** {status}\n**Received:** {receivedAgo}\n**ID:** `{update.Id}`",
                inline: false);
        }

        if (pendingUpdates.Count > 10)
        {
            embedBuilder.WithDescription($"⚠️ Showing first 10 of {pendingUpdates.Count} updates");
        }

        await RespondAsync(embed: embedBuilder.Build(), ephemeral: true);
        
        logger.LogInformation("Listed {Count} pending updates for user {User}", 
            pendingUpdates.Count, 
            Context.User.Username);
    }

    [SlashCommand("update", "Triggers an update for a specific container")]
    public async Task UpdateAsync(
        [Summary("container", "The name of the container to update")] string container)
    {
        logger.LogInformation("update command invoked by {User} for container {Container}", 
            Context.User.Username, container);

        // Defer the response as this could take a while
        await DeferAsync(ephemeral: false);

        // Find pending updates that match the container name
        var pendingUpdates = updateTracker.GetPendingUpdates()
            .Where(u => u.Payload.Metadata?.CtnNames?.Contains(container, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (pendingUpdates.Count == 0)
        {
            await FollowupAsync($"No pending updates found for container matching `{container}`.", ephemeral: true);
            return;
        }

        if (pendingUpdates.Count > 1)
        {
            // Multiple matches - show them and ask user to be more specific
            var matchList = string.Join("\n", pendingUpdates.Select(u => 
                $"- {u.Payload.Image} (ID: `{u.Id}`)"));
            
            await FollowupAsync(
                $"Multiple pending updates match `{container}`:\n{matchList}\n\nPlease use the Update button on the specific Discord message instead.",
                ephemeral: true);
            return;
        }

        // Single match found
        var update = pendingUpdates[0];
        var imageName = update.Payload.Image ?? "unknown";
        var containerName = update.Payload.Metadata?.CtnNames?.TrimStart('/') ?? container;

        // Resolve compose project via docker inspect
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

            await FollowupAsync(embed: errorEmbed);
            return;
        }

        var (_, _, serviceName, _) = composeInfo;

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

            await FollowupAsync(embed: exceptionEmbed);
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

            // Build success embed
            var successEmbedBuilder = new EmbedBuilder()
                .WithTitle("✅ Updated Successfully")
                .WithDescription($"Container **{containerName}** has been updated")
                .WithColor(0x00FF00) // Green
                .AddField("Image", imageName, inline: false)
                .AddField("Service", serviceName, inline: true)
                .AddField("Project", composeInfo.ProjectName, inline: true)
                .AddField("Triggered By", Context.User.Mention, inline: true)
                .AddField("Duration", $"{result.Duration.TotalSeconds:F2}s", inline: true);

            if (sourceUpdated)
            {
                successEmbedBuilder.AddField("Source Updated", $"✅ `{Path.GetFileName(composeInfo.ConfigFile)}`", inline: true);
            }

            var successEmbed = successEmbedBuilder
                .WithTimestamp(DateTimeOffset.UtcNow)
                .WithFooter($"Update ID: {update.Id}")
                .Build();

            // Build details embed
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

            await FollowupAsync(embeds: new[] { successEmbed, detailsEmbedBuilder.Build() });

            logger.LogInformation(
                "Update completed successfully for container {Container}, update {UpdateId} in {Duration:F2}s",
                container,
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

            await FollowupAsync(embeds: new[] { failureEmbed, errorDetailsEmbed });

            logger.LogError(
                "Update failed for container {Container}, update {UpdateId}. Error: {Error}",
                container,
                update.Id,
                result.ErrorOutput);
        }
    }

    /// <summary>
    /// Truncates text to fit within Discord's field value limits.
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
    /// Helper method to format relative time (e.g., "2 minutes ago")
    /// </summary>
    private static string GetRelativeTime(DateTime dateTime)
    {
        var timeSpan = DateTime.UtcNow - dateTime;

        if (timeSpan.TotalMinutes < 1)
            return "Just now";
        if (timeSpan.TotalMinutes < 60)
            return $"{(int)timeSpan.TotalMinutes} minute(s) ago";
        if (timeSpan.TotalHours < 24)
            return $"{(int)timeSpan.TotalHours} hour(s) ago";
        
        return $"{(int)timeSpan.TotalDays} day(s) ago";
    }
}
