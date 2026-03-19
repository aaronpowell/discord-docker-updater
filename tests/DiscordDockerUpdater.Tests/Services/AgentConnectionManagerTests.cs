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
}
