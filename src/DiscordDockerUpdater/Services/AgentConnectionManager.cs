using System.Collections.Concurrent;
using DiscordDockerUpdater.Models;

namespace DiscordDockerUpdater.Services;

/// <summary>
/// Tracks SignalR-connected agents and provides hostname-based lookup for routing update commands.
/// Thread-safe — agents may connect and disconnect concurrently.
/// </summary>
public class AgentConnectionManager
{
    private readonly ConcurrentDictionary<string, ConnectedAgent> _byConnectionId = new();
    private readonly ConcurrentDictionary<string, string> _hostnameToConnectionId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _friendlyNameToHostname = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<AgentConnectionManager> _logger;

    public AgentConnectionManager(ILogger<AgentConnectionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a newly connected agent. If an agent with the same hostname is already
    /// registered, the old connection is evicted (last-connect wins).
    /// </summary>
    public void RegisterAgent(string connectionId, AgentRegistration registration)
    {
        var agent = new ConnectedAgent(connectionId, registration, DateTimeOffset.UtcNow);

        // Evict any previous connection for this hostname
        if (_hostnameToConnectionId.TryGetValue(registration.Hostname, out var oldConnectionId)
            && oldConnectionId != connectionId)
        {
            if (_byConnectionId.TryRemove(oldConnectionId, out var evictedAgent)
                && !string.IsNullOrWhiteSpace(evictedAgent.Registration.FriendlyName))
            {
                _friendlyNameToHostname.TryRemove(
                    new KeyValuePair<string, string>(evictedAgent.Registration.FriendlyName, registration.Hostname));
            }

            _logger.LogWarning(
                "Evicting previous connection {OldConnectionId} for hostname {Hostname} in favour of {NewConnectionId}",
                oldConnectionId, registration.Hostname, connectionId);
        }

        _byConnectionId[connectionId] = agent;
        _hostnameToConnectionId[registration.Hostname] = connectionId;

        if (!string.IsNullOrWhiteSpace(registration.FriendlyName))
        {
            _friendlyNameToHostname[registration.FriendlyName] = registration.Hostname;
        }

        _logger.LogInformation(
            "Agent registered: hostname={Hostname}, displayName={DisplayName}, connectionId={ConnectionId}, containers={ContainerCount}",
            registration.Hostname, registration.DisplayName, connectionId, registration.Containers?.Count ?? 0);
    }

    /// <summary>
    /// Removes an agent when its SignalR connection drops.
    /// </summary>
    public void UnregisterAgent(string connectionId)
    {
        if (_byConnectionId.TryRemove(connectionId, out var agent))
        {
            // Only remove the hostname mapping if it still points to this connection
            _hostnameToConnectionId.TryRemove(
                new KeyValuePair<string, string>(agent.Registration.Hostname, connectionId));

            // Remove friendly name mapping if set
            if (!string.IsNullOrWhiteSpace(agent.Registration.FriendlyName))
            {
                _friendlyNameToHostname.TryRemove(
                    new KeyValuePair<string, string>(agent.Registration.FriendlyName, agent.Registration.Hostname));
            }

            _logger.LogInformation(
                "Agent unregistered: hostname={Hostname}, connectionId={ConnectionId}",
                agent.Registration.Hostname, connectionId);
        }
    }

    /// <summary>
    /// Looks up the SignalR connection ID for a given DIUN hostname.
    /// Returns false if no agent is connected for that hostname.
    /// </summary>
    public bool TryGetConnectionId(string hostname, out string? connectionId)
    {
        return _hostnameToConnectionId.TryGetValue(hostname, out connectionId);
    }

    /// <summary>
    /// Quick check whether an agent is connected for the given hostname.
    /// </summary>
    public bool IsAgentConnected(string hostname)
    {
        return _hostnameToConnectionId.ContainsKey(hostname);
    }

    /// <summary>
    /// Finds a connected agent by hostname or friendly name (case-insensitive).
    /// Hostname lookup takes precedence over friendly name.
    /// Returns false if no matching agent is found.
    /// </summary>
    public bool TryFindAgent(string nameOrHostname, out ConnectedAgent? agent)
    {
        // Try hostname lookup first
        if (_hostnameToConnectionId.TryGetValue(nameOrHostname, out var connId)
            && _byConnectionId.TryGetValue(connId, out agent))
        {
            return true;
        }

        // Try friendly name lookup → hostname → connectionId
        if (_friendlyNameToHostname.TryGetValue(nameOrHostname, out var hostname)
            && _hostnameToConnectionId.TryGetValue(hostname, out connId)
            && _byConnectionId.TryGetValue(connId, out agent))
        {
            return true;
        }

        agent = null;
        return false;
    }

    /// <summary>
    /// Returns a snapshot of all currently connected agents.
    /// </summary>
    public IReadOnlyList<ConnectedAgent> GetConnectedAgents()
    {
        return _byConnectionId.Values.ToList();
    }
}

/// <summary>
/// Represents a connected agent with its registration metadata and connection time.
/// </summary>
public record ConnectedAgent(string ConnectionId, AgentRegistration Registration, DateTimeOffset ConnectedAt);
