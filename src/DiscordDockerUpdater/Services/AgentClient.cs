using DiscordDockerUpdater.Hubs;
using DiscordDockerUpdater.Models;
using Microsoft.AspNetCore.SignalR;

namespace DiscordDockerUpdater.Services;

/// <summary>
/// Communicates with remote agents via SignalR hub.
/// Agents self-register by connecting to the hub — no static registry needed.
/// </summary>
public class AgentClient(
    IHubContext<AgentHub> hubContext,
    AgentConnectionManager connectionManager,
    ILogger<AgentClient> logger)
{
    /// <summary>
    /// Returns true if a connected agent exists for the given DIUN hostname.
    /// A null or empty hostname always returns false (treated as local).
    /// </summary>
    public bool IsAgentConnected(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return false;

        return connectionManager.IsAgentConnected(hostname);
    }

    /// <summary>
    /// Returns the display name (friendly name if set, otherwise hostname) for the agent
    /// connected under the given hostname. Returns the raw hostname if no agent is found.
    /// </summary>
    public string GetDisplayNameForHost(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return hostname ?? "local";

        return connectionManager.TryFindAgent(hostname, out var agent) && agent is not null
            ? agent.Registration.DisplayName
            : hostname;
    }

    /// <summary>
    /// Sends an update command to the agent registered for the given hostname
    /// and waits for the result.
    /// </summary>
    /// <exception cref="InvalidOperationException">No agent connected for the hostname.</exception>
    public async Task<AgentUpdateResponse> SendUpdateAsync(string hostname, AgentUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (!connectionManager.TryGetConnectionId(hostname, out var connectionId) || connectionId is null)
        {
            throw new InvalidOperationException($"No agent is connected for hostname '{hostname}'.");
        }

        logger.LogInformation(
            "Sending update command to agent {Hostname} (connection {ConnectionId}) for container {Container}",
            hostname, connectionId, request.ContainerName);

        try
        {
            var response = await hubContext.Clients.Client(connectionId)
                .InvokeAsync<AgentUpdateResponse>("ExecuteUpdate", request, cancellationToken);

            return response ?? new AgentUpdateResponse { Success = false, ErrorOutput = "Empty response from agent" };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to invoke update on agent {Hostname} (connection {ConnectionId})", hostname, connectionId);
            throw;
        }
    }
}
