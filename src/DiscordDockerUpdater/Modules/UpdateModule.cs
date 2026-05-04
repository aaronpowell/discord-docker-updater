using System.Text.Json;
using Discord;
using Discord.Interactions;
using DiscordDockerUpdater.Configuration;
using DiscordDockerUpdater.Services;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;

namespace DiscordDockerUpdater.Modules;

public class UpdateModule(
    ILogger<UpdateModule> logger,
    UpdateTracker updateTracker,
    ContainerInspector containerInspector,
    DockerComposeExecutor composeExecutor,
    ComposeFileUpdater composeFileUpdater,
    IDockerClient dockerClient,
    IOptions<BotConfiguration> botConfig) : InteractionModuleBase<SocketInteractionContext>
{

    [SlashCommand("status", "Show what Diun is actually tracking, plus blind spots and pending updates")]
    public async Task StatusAsync()
    {
        logger.LogInformation("status command invoked by {User}", Context.User.Username);
        await DeferAsync(ephemeral: true);

        // Query Diun directly for ground truth — what's actually in its DB,
        // not just what labels suggest. Avoids the "we thought it was watched
        // but it wasn't" failure mode (auth issues, scan timing, stale state).
        Dictionary<string, (string Tag, string Digest)> tracked;
        try
        {
            tracked = await GetDiunTrackedImagesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to query Diun for tracked images");
            await FollowupAsync(
                $"❌ Couldn't query Diun's tracking database: `{ex.Message}`. Is the `diun` container running?",
                ephemeral: true);
            return;
        }

        var containers = await dockerClient.Containers.ListContainersAsync(
            new ContainersListParameters { All = false });

        var pending = updateTracker.GetPendingUpdates()
            .Select(u => (u.Payload.Metadata?.CtnNames ?? "").TrimStart('/'))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var trackedSet = new HashSet<string>(tracked.Keys, StringComparer.OrdinalIgnoreCase);
        var matchedTracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rows = containers
            .Select(c =>
            {
                var name = (c.Names?.FirstOrDefault() ?? c.ID).TrimStart('/');
                var image = c.Image ?? "";
                var normalized = NormalizeImageName(image);
                var optedOut = c.Labels != null
                    && c.Labels.TryGetValue("diun.enable", out var v)
                    && string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
                var isTracked = !optedOut && trackedSet.Contains(normalized);
                if (isTracked) matchedTracked.Add(normalized);
                return (Name: name, Image: image, Normalized: normalized,
                        OptedOut: optedOut, Tracked: isTracked,
                        HasPending: pending.Contains(name));
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var watched = rows.Where(r => r.Tracked).ToList();
        var optedOutRows = rows.Where(r => r.OptedOut).ToList();
        var blindSpots = rows.Where(r => !r.OptedOut && !r.Tracked).ToList();
        var stale = tracked.Keys.Where(k => !matchedTracked.Contains(k)).ToList();
        var pendingCount = watched.Count(r => r.HasPending);

        var embed = new EmbedBuilder()
            .WithTitle("🐳 Diun Tracking Status")
            .WithColor(new Color(blindSpots.Count > 0 ? 0xFFA500u : 0x00CC00u))
            .WithDescription(
                pendingCount > 0
                    ? $"⏳ **{pendingCount}** pending update(s) — see `/list-updates` for actions."
                    : "No pending updates.")
            .WithTimestamp(DateTimeOffset.UtcNow)
            .WithFooter(
                $"{watched.Count} watched / {rows.Count} running • {tracked.Count} in Diun's DB");

        // Discord embeds cap at 25 fields and 1024 chars per field value.
        // Reserve up to 3 fields for the optional sections (blind spots,
        // opted out, stale) so we never blow the cap and crash with
        // ArgumentException on Build(). With 5 entries per watched-chunk
        // field that gives us 22 * 5 = 110 watched containers visible
        // before we have to truncate — generous for any realistic fleet.
        const int perChunk = 5;
        const int maxFields = 25;
        const int reservedForOtherSections = 3;
        var maxWatchedShown = (maxFields - reservedForOtherSections) * perChunk;
        var displayedWatched = watched.Take(maxWatchedShown).ToList();
        var watchedTruncated = watched.Count - displayedWatched.Count;

        if (watched.Count == 0)
        {
            embed.AddField("✅ Watched (0)",
                "*Diun is not tracking any of the running containers — check Diun's logs and auth config.*",
                inline: false);
        }
        else
        {
            for (var i = 0; i < displayedWatched.Count; i += perChunk)
            {
                var chunk = displayedWatched.Skip(i).Take(perChunk);
                var lines = chunk.Select(r =>
                {
                    var marker = r.HasPending ? "⏳" : "✅";
                    var trackedTag = tracked.TryGetValue(r.Normalized, out var t) ? t.Tag : "?";
                    return $"{marker} `{r.Name}` — `{r.Image}` → tag `{trackedTag}`";
                });
                var heading = i == 0
                    ? (watchedTruncated > 0
                        ? $"✅ Watched ({watched.Count}, showing first {displayedWatched.Count})"
                        : $"✅ Watched ({watched.Count})")
                    : "​";
                embed.AddField(
                    name: heading,
                    value: TruncateForDiscord(string.Join("\n", lines), 1024),
                    inline: false);
            }
        }

        if (blindSpots.Count > 0)
        {
            var lines = blindSpots.Select(r => $"❌ `{r.Name}` — `{r.Image}`");
            embed.AddField(
                name: $"⚠️ Blind spots ({blindSpots.Count}) — running but NOT in Diun's DB",
                value: TruncateForDiscord(string.Join("\n", lines), 1024),
                inline: false);
        }

        if (optedOutRows.Count > 0)
        {
            var names = string.Join(", ", optedOutRows.Select(r => $"`{r.Name}`"));
            embed.AddField(
                name: $"➖ Opted out ({optedOutRows.Count}) — `diun.enable=false`",
                value: TruncateForDiscord(names, 1024),
                inline: false);
        }

        if (stale.Count > 0)
        {
            var lines = stale.Select(s => $"`{s}` (no matching running container)");
            embed.AddField(
                name: $"🗑️ Stale Diun entries ({stale.Count})",
                value: TruncateForDiscord(string.Join("\n", lines), 1024),
                inline: false);
        }

        await FollowupAsync(embed: embed.Build(), ephemeral: true);

        logger.LogInformation(
            "Reported status to {User}: watched={Watched} blind={Blind} opted_out={OptedOut} stale={Stale} pending={Pending}",
            Context.User.Username, watched.Count, blindSpots.Count, optedOutRows.Count, stale.Count, pendingCount);
    }

    /// <summary>
    /// Queries Diun for its authoritative tracked-images list by exec'ing
    /// `diun image list --raw` inside the configured Diun container and
    /// parsing the JSON. Returns a map of full registry-qualified image name
    /// → (latest tag, digest). Bounded by a 30-second timeout so a hung Diun
    /// can't block the slash command past Discord's interaction window.
    /// </summary>
    private async Task<Dictionary<string, (string Tag, string Digest)>> GetDiunTrackedImagesAsync()
    {
        var diunContainer = botConfig.Value.DiunContainerName;
        if (string.IsNullOrWhiteSpace(diunContainer))
        {
            throw new InvalidOperationException(
                "Bot:DiunContainerName is not configured.");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var execResp = await dockerClient.Exec.ExecCreateContainerAsync(diunContainer,
            new ContainerExecCreateParameters
            {
                Cmd = new[] { "diun", "image", "list", "--raw" },
                AttachStdout = true,
                AttachStderr = true,
            }, cts.Token);

        using var stream = await dockerClient.Exec.StartAndAttachContainerExecAsync(execResp.ID, false, cts.Token);
        string stdout, stderr;
        try
        {
            (stdout, stderr) = await stream.ReadOutputToEndAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"`diun image list --raw` did not return within 30s — is the `{diunContainer}` container responsive?");
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException(
                $"empty output from `diun image list --raw`. stderr: {stderr.Trim()}");
        }

        using var doc = JsonDocument.Parse(stdout);
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        // Diun emits `{"images": [...]}` today; fall back to a bare array
        // if that ever flips, and log a warning so we notice the schema
        // mismatch instead of silently flooding /status with blind spots.
        JsonElement imagesEl;
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("images", out imagesEl))
        {
            // happy path
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            imagesEl = doc.RootElement;
        }
        else
        {
            logger.LogWarning(
                "`diun image list --raw` returned unexpected JSON shape (root kind={Kind}); /status will show all containers as blind spots until this is investigated",
                doc.RootElement.ValueKind);
            return result;
        }

        foreach (var img in imagesEl.EnumerateArray())
        {
            var name = img.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name)) continue;
            var tag = "";
            var digest = "";
            if (img.TryGetProperty("latest", out var latestEl))
            {
                if (latestEl.TryGetProperty("tag", out var tagEl)) tag = tagEl.GetString() ?? "";
                if (latestEl.TryGetProperty("digest", out var digestEl)) digest = digestEl.GetString() ?? "";
            }
            result[name] = (tag, digest);
        }
        return result;
    }

    /// <summary>
    /// Normalizes a Docker image reference to the form Diun stores it in
    /// (full registry path, no tag/digest). Examples:
    ///   `crazymax/diun:latest`   → `docker.io/crazymax/diun`
    ///   `nginx:1.25`             → `docker.io/library/nginx`
    ///   `ghcr.io/foo/bar:latest` → `ghcr.io/foo/bar`
    ///   `discord-docker-updater:local` → `docker.io/library/discord-docker-updater`
    ///     (Diun won't have this — locally-built — so it'll show as a blind
    ///     spot, which is correct.)
    /// </summary>
    private static string NormalizeImageName(string image)
    {
        if (string.IsNullOrEmpty(image)) return "";
        // Strip digest first (everything after @)
        var atIdx = image.IndexOf('@');
        if (atIdx > 0) image = image[..atIdx];
        // Strip tag — last colon, but only if it's *after* the last slash
        // (so `localhost:5000/foo` keeps its port).
        var lastSlashIdx = image.LastIndexOf('/');
        var lastColonIdx = image.LastIndexOf(':');
        if (lastColonIdx > lastSlashIdx) image = image[..lastColonIdx];
        // Add docker.io prefix if no registry segment
        if (!image.Contains('/'))
        {
            return $"docker.io/library/{image}";
        }
        var firstSegment = image[..image.IndexOf('/')];
        if (!firstSegment.Contains('.') && !firstSegment.Contains(':') && firstSegment != "localhost")
        {
            return $"docker.io/{image}";
        }
        return image;
    }

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
