using DiscordDockerUpdater.Configuration;
using DiscordDockerUpdater.Models;
using DiscordDockerUpdater.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace DiscordDockerUpdater.Hubs;

/// <summary>
/// SignalR hub that agents connect to for receiving update commands.
/// Agents call <see cref="RegisterAgent"/> after connecting to identify themselves.
/// The bot sends update commands via <see cref="IHubContext{AgentHub}"/> using client invocation.
/// </summary>
public class AgentHub(
    AgentConnectionManager connectionManager,
    IOptions<BotConfiguration> config,
    ILogger<AgentHub> logger) : Hub
{
    /// <summary>
    /// Called by an agent after connecting to register its hostname and metadata.
    /// </summary>
    public void RegisterAgent(AgentRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.Hostname))
        {
            logger.LogWarning("Agent {ConnectionId} tried to register with empty hostname", Context.ConnectionId);
            throw new HubException("Hostname is required for agent registration.");
        }

        connectionManager.RegisterAgent(Context.ConnectionId, registration);
    }

    public override async Task OnConnectedAsync()
    {
        // Validate agent token if configured
        var agentToken = config.Value.AgentToken;
        if (!string.IsNullOrWhiteSpace(agentToken))
        {
            var providedToken = Context.GetHttpContext()?.Request.Query["access_token"].ToString();
            if (providedToken != agentToken)
            {
                logger.LogWarning("Agent connection rejected: invalid token from {ConnectionId}", Context.ConnectionId);
                Context.Abort();
                return;
            }
        }

        logger.LogInformation("Agent connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connectionManager.UnregisterAgent(Context.ConnectionId);

        if (exception != null)
        {
            logger.LogWarning(exception, "Agent disconnected with error: {ConnectionId}", Context.ConnectionId);
        }
        else
        {
            logger.LogInformation("Agent disconnected: {ConnectionId}", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
