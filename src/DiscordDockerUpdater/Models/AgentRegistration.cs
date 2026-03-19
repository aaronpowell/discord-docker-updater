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
