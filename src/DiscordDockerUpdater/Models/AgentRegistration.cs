namespace DiscordDockerUpdater.Models;

/// <summary>
/// Registration payload sent by an agent when it connects to the bot's SignalR hub.
/// Contains the agent's identity and metadata for routing and status display.
/// </summary>
public class AgentRegistration
{
    /// <summary>
    /// The hostname as reported by DIUN — used to route update commands to the correct agent.
    /// </summary>
    public string Hostname { get; set; } = "";

    /// <summary>
    /// Optional human-readable name for this agent. When set, it is shown in Discord messages
    /// and accepted in command lookups instead of the machine hostname.
    /// Falls back to <see cref="Hostname"/> when empty.
    /// Configured via <c>Bot__FriendlyName</c> in agent mode.
    /// </summary>
    public string? FriendlyName { get; set; }

    /// <summary>
    /// The name to display in Discord messages and commands.
    /// Returns <see cref="FriendlyName"/> if set, otherwise <see cref="Hostname"/>.
    /// </summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName : Hostname;

    /// <summary>
    /// Names of Docker containers currently running on the agent's host.
    /// </summary>
    public List<string>? Containers { get; set; }

    /// <summary>
    /// Docker engine version on the agent's host.
    /// </summary>
    public string? DockerVersion { get; set; }

    /// <summary>
    /// OS description of the agent's host (e.g., "Linux 6.1.0 #1 SMP Debian").
    /// </summary>
    public string? OSDescription { get; set; }
}
