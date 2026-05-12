using DiscordDockerUpdater.Models;
using DiscordDockerUpdater.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace DiscordDockerUpdater.Tests.Services;

public class AgentConnectionManagerTests
{
    private readonly AgentConnectionManager _manager;

    public AgentConnectionManagerTests()
    {
        var logger = Mock.Of<ILogger<AgentConnectionManager>>();
        _manager = new AgentConnectionManager(logger);
    }

    [Fact]
    public void RegisterAgent_StoresAgent_CanLookUpByHostname()
    {
        var registration = new AgentRegistration { Hostname = "server1" };
        _manager.RegisterAgent("conn-1", registration);

        Assert.True(_manager.TryGetConnectionId("server1", out var connectionId));
        Assert.Equal("conn-1", connectionId);
    }

    [Fact]
    public void IsAgentConnected_ReturnsFalse_WhenNoAgentRegistered()
    {
        Assert.False(_manager.IsAgentConnected("unknown-host"));
    }

    [Fact]
    public void IsAgentConnected_ReturnsTrue_WhenAgentRegistered()
    {
        _manager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "server1" });

        Assert.True(_manager.IsAgentConnected("server1"));
    }

    [Fact]
    public void UnregisterAgent_RemovesAgent()
    {
        _manager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "server1" });
        _manager.UnregisterAgent("conn-1");

        Assert.False(_manager.IsAgentConnected("server1"));
        Assert.False(_manager.TryGetConnectionId("server1", out _));
    }

    [Fact]
    public void UnregisterAgent_NoOp_WhenConnectionIdNotFound()
    {
        _manager.UnregisterAgent("nonexistent");
        // Should not throw
    }

    [Fact]
    public void RegisterAgent_EvictsPreviousConnection_ForSameHostname()
    {
        _manager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "server1" });
        _manager.RegisterAgent("conn-2", new AgentRegistration { Hostname = "server1" });

        Assert.True(_manager.TryGetConnectionId("server1", out var connectionId));
        Assert.Equal("conn-2", connectionId);

        // Old connection should be gone
        var agents = _manager.GetConnectedAgents();
        Assert.Single(agents);
        Assert.Equal("conn-2", agents[0].ConnectionId);
    }

    [Fact]
    public void GetConnectedAgents_ReturnsAllAgents()
    {
        _manager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "server1" });
        _manager.RegisterAgent("conn-2", new AgentRegistration { Hostname = "server2" });
        _manager.RegisterAgent("conn-3", new AgentRegistration { Hostname = "server3" });

        var agents = _manager.GetConnectedAgents();
        Assert.Equal(3, agents.Count);
    }

    [Fact]
    public void TryGetConnectionId_IsCaseInsensitive()
    {
        _manager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "ServerOne" });

        Assert.True(_manager.TryGetConnectionId("serverone", out var connectionId));
        Assert.Equal("conn-1", connectionId);
        Assert.True(_manager.TryGetConnectionId("SERVERONE", out _));
    }

    [Fact]
    public void UnregisterAgent_DoesNotRemoveHostname_IfConnectionIdDoesNotMatch()
    {
        // Register conn-1, then register conn-2 for the same hostname (evicts conn-1 from _byConnectionId).
        // Unregistering conn-1 should NOT remove the hostname mapping because it now points to conn-2.
        _manager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "server1" });
        _manager.RegisterAgent("conn-2", new AgentRegistration { Hostname = "server1" });

        _manager.UnregisterAgent("conn-1");

        // conn-2 should still be registered
        Assert.True(_manager.IsAgentConnected("server1"));
        Assert.True(_manager.TryGetConnectionId("server1", out var id));
        Assert.Equal("conn-2", id);
    }

    [Fact]
    public void RegisterAgent_StoresMetadata()
    {
        var registration = new AgentRegistration
        {
            Hostname = "server1",
            Containers = new List<string> { "nginx", "redis" },
            DockerVersion = "24.0.7",
            OSDescription = "Linux 6.1.0"
        };

        _manager.RegisterAgent("conn-1", registration);

        var agents = _manager.GetConnectedAgents();
        var agent = Assert.Single(agents);
        Assert.Equal("server1", agent.Registration.Hostname);
        Assert.Equal(2, agent.Registration.Containers!.Count);
        Assert.Equal("24.0.7", agent.Registration.DockerVersion);
        Assert.Equal("Linux 6.1.0", agent.Registration.OSDescription);
        Assert.True(agent.ConnectedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void TryFindAgent_FindsByHostname()
    {
        _manager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "server1" });

        Assert.True(_manager.TryFindAgent("server1", out var agent));
        Assert.NotNull(agent);
        Assert.Equal("conn-1", agent!.ConnectionId);
    }

    [Fact]
    public void TryFindAgent_FindsByFriendlyName()
    {
        _manager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "server1", FriendlyName = "home-server" });

        Assert.True(_manager.TryFindAgent("home-server", out var agent));
        Assert.NotNull(agent);
        Assert.Equal("conn-1", agent!.ConnectionId);
        Assert.Equal("server1", agent.Registration.Hostname);
    }

    [Fact]
    public void TryFindAgent_ReturnsFalse_WhenNotFound()
    {
        Assert.False(_manager.TryFindAgent("unknown", out var agent));
        Assert.Null(agent);
    }

    [Fact]
    public void TryFindAgent_IsCaseInsensitive_ForBothHostnameAndFriendlyName()
    {
        _manager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "ServerOne", FriendlyName = "Home-Server" });

        Assert.True(_manager.TryFindAgent("serverone", out var byHostname));
        Assert.Equal("conn-1", byHostname!.ConnectionId);

        Assert.True(_manager.TryFindAgent("HOME-SERVER", out var byFriendlyName));
        Assert.Equal("conn-1", byFriendlyName!.ConnectionId);
    }

    [Fact]
    public void TryFindAgent_HostnameTakesPrecedenceOverFriendlyName()
    {
        // Agent1 has hostname "home-server" (no friendly name)
        // Agent2 has hostname "server2" with friendly name "home-server"
        // Lookup "home-server" should return Agent1 (hostname match wins)
        _manager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "home-server" });
        _manager.RegisterAgent("conn-2", new AgentRegistration { Hostname = "server2", FriendlyName = "home-server" });

        Assert.True(_manager.TryFindAgent("home-server", out var agent));
        Assert.Equal("conn-1", agent!.ConnectionId);
    }

    [Fact]
    public void UnregisterAgent_RemovesFriendlyNameIndex()
    {
        _manager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "server1", FriendlyName = "my-server" });
        _manager.UnregisterAgent("conn-1");

        Assert.False(_manager.TryFindAgent("my-server", out _));
        Assert.False(_manager.TryFindAgent("server1", out _));
    }

    [Fact]
    public void RegisterAgent_EvictionCleansFriendlyNameIndex()
    {
        // Register conn-1 with friendly name "old-name"
        _manager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "server1", FriendlyName = "old-name" });

        // Re-register same hostname with conn-2, different friendly name
        _manager.RegisterAgent("conn-2", new AgentRegistration { Hostname = "server1", FriendlyName = "new-name" });

        // old-name should no longer resolve
        Assert.False(_manager.TryFindAgent("old-name", out _));

        // new-name should resolve to conn-2
        Assert.True(_manager.TryFindAgent("new-name", out var agent));
        Assert.Equal("conn-2", agent!.ConnectionId);
    }

    [Fact]
    public void DisplayName_ReturnsFriendlyName_WhenSet()
    {
        var registration = new AgentRegistration { Hostname = "server1", FriendlyName = "my-server" };
        Assert.Equal("my-server", registration.DisplayName);
    }

    [Fact]
    public void DisplayName_ReturnsHostname_WhenFriendlyNameIsNull()
    {
        var registration = new AgentRegistration { Hostname = "server1" };
        Assert.Equal("server1", registration.DisplayName);
    }

    [Fact]
    public void DisplayName_ReturnsHostname_WhenFriendlyNameIsWhitespace()
    {
        var registration = new AgentRegistration { Hostname = "server1", FriendlyName = "   " };
        Assert.Equal("server1", registration.DisplayName);
    }
}
