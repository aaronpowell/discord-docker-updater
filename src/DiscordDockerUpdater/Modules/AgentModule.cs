using Discord;
using Discord.Interactions;
using DiscordDockerUpdater.Services;

namespace DiscordDockerUpdater.Modules;

public class AgentModule(
    ILogger<AgentModule> logger,
    AgentConnectionManager connectionManager,
    UpdateTracker updateTracker) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("agents", "Lists all connected remote agents")]
    public async Task ListAgentsAsync()
    {
        logger.LogInformation("agents command invoked by {User}", Context.User.Username);

        var agents = connectionManager.GetConnectedAgents();

        if (agents.Count == 0)
        {
            await RespondAsync("No agents are currently connected.", ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("🌐 Connected Agents")
            .WithColor(0x0099FF)
            .WithTimestamp(DateTimeOffset.UtcNow)
            .WithFooter($"{agents.Count} agent(s) connected");

        foreach (var agent in agents)
        {
            var reg = agent.Registration;
            var uptime = GetRelativeTime(agent.ConnectedAt);
            var containerCount = reg.Containers?.Count ?? 0;
            var containerList = containerCount > 0
                ? string.Join(", ", reg.Containers!.Take(10).Select(c => $"`{c}`"))
                : "_none reported_";

            if (containerCount > 10)
                containerList += $" … and {containerCount - 10} more";

            var pendingCount = updateTracker.GetPendingUpdatesForHost(reg.Hostname).Count();

            embed.AddField(
                name: $"🖥️ {reg.Hostname}",
                value: $"**Docker:** {reg.DockerVersion ?? "N/A"}\n" +
                       $"**OS:** {reg.OSDescription ?? "N/A"}\n" +
                       $"**Containers:** {containerCount} — {containerList}\n" +
                       $"**Pending Updates:** {pendingCount}\n" +
                       $"**Connected:** {uptime}",
                inline: false);
        }

        await RespondAsync(embed: embed.Build(), ephemeral: true);
    }

    [SlashCommand("agent-info", "Shows details for a specific connected agent")]
    public async Task AgentInfoAsync(
        [Summary("hostname", "The hostname of the agent to query")] string hostname)
    {
        logger.LogInformation("agent-info command invoked by {User} for host {Hostname}",
            Context.User.Username, hostname);

        if (!connectionManager.IsAgentConnected(hostname))
        {
            await RespondAsync($"No agent is currently connected for hostname `{hostname}`.", ephemeral: true);
            return;
        }

        var agents = connectionManager.GetConnectedAgents();
        var agent = agents.FirstOrDefault(a =>
            string.Equals(a.Registration.Hostname, hostname, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
        {
            await RespondAsync($"Agent `{hostname}` not found.", ephemeral: true);
            return;
        }

        var reg = agent.Registration;

        // Agent info embed
        var infoEmbed = new EmbedBuilder()
            .WithTitle($"🖥️ Agent: {reg.Hostname}")
            .WithColor(0x00CC66)
            .AddField("Docker Version", reg.DockerVersion ?? "N/A", inline: true)
            .AddField("OS", reg.OSDescription ?? "N/A", inline: true)
            .AddField("Connected", GetRelativeTime(agent.ConnectedAt), inline: true);

        if (reg.Containers is { Count: > 0 })
        {
            var containerList = string.Join("\n", reg.Containers.Select(c => $"• `{c}`"));
            if (containerList.Length > 1024)
                containerList = containerList[..1020] + "\n…";
            infoEmbed.AddField($"Containers ({reg.Containers.Count})", containerList, inline: false);
        }
        else
        {
            infoEmbed.AddField("Containers", "_none reported_", inline: false);
        }

        // Pending updates for this host
        var pendingUpdates = updateTracker.GetPendingUpdatesForHost(reg.Hostname).ToList();

        if (pendingUpdates.Count > 0)
        {
            var updatesEmbed = new EmbedBuilder()
                .WithTitle($"📋 Pending Updates on {reg.Hostname}")
                .WithColor(0xFF9900)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .WithFooter($"{pendingUpdates.Count} pending update(s)");

            foreach (var update in pendingUpdates.Take(10))
            {
                var payload = update.Payload;
                var imageName = payload.Image ?? "Unknown";
                var containerName = payload.Metadata?.CtnNames?.TrimStart('/') ?? "N/A";
                var receivedAgo = GetRelativeTime(update.ReceivedAt);

                updatesEmbed.AddField(
                    name: $"🐳 {imageName}",
                    value: $"**Container:** {containerName}\n**Received:** {receivedAgo}\n**ID:** `{update.Id}`",
                    inline: false);
            }

            if (pendingUpdates.Count > 10)
                updatesEmbed.WithDescription($"⚠️ Showing first 10 of {pendingUpdates.Count} updates");

            await RespondAsync(embeds: new[] { infoEmbed.Build(), updatesEmbed.Build() }, ephemeral: true);
        }
        else
        {
            infoEmbed.AddField("Pending Updates", "✅ No pending updates for this host", inline: false);
            infoEmbed.WithTimestamp(DateTimeOffset.UtcNow);
            await RespondAsync(embed: infoEmbed.Build(), ephemeral: true);
        }
    }

    private static string GetRelativeTime(DateTimeOffset dateTime)
    {
        var timeSpan = DateTimeOffset.UtcNow - dateTime;

        if (timeSpan.TotalMinutes < 1) return "Just now";
        if (timeSpan.TotalMinutes < 60) return $"{(int)timeSpan.TotalMinutes} minute(s) ago";
        if (timeSpan.TotalHours < 24) return $"{(int)timeSpan.TotalHours} hour(s) ago";
        return $"{(int)timeSpan.TotalDays} day(s) ago";
    }

    private static string GetRelativeTime(DateTime dateTime)
    {
        return GetRelativeTime(new DateTimeOffset(dateTime, TimeSpan.Zero));
    }
}
