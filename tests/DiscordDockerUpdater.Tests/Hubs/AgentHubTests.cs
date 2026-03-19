using DiscordDockerUpdater.Configuration;
using DiscordDockerUpdater.Hubs;
using DiscordDockerUpdater.Models;
using DiscordDockerUpdater.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace DiscordDockerUpdater.Tests.Hubs;

public class AgentHubTests
{
    private readonly AgentConnectionManager _connectionManager;
    private readonly AgentHub _hub;

    public AgentHubTests()
    {
        var logger = Mock.Of<ILogger<AgentConnectionManager>>();
        _connectionManager = new AgentConnectionManager(logger);

        var config = Options.Create(new BotConfiguration());
        var hubLogger = Mock.Of<ILogger<AgentHub>>();
        _hub = new AgentHub(_connectionManager, config, hubLogger);
    }

    [Fact]
    public void RegisterAgent_StoresAgentInConnectionManager()
    {
        SetupHubContext("conn-test");

        var registration = new AgentRegistration { Hostname = "test-host" };
        _hub.RegisterAgent(registration);

        Assert.True(_connectionManager.IsAgentConnected("test-host"));
        Assert.True(_connectionManager.TryGetConnectionId("test-host", out var id));
        Assert.Equal("conn-test", id);
    }

    [Fact]
    public void RegisterAgent_ThrowsHubException_WhenHostnameIsEmpty()
    {
        SetupHubContext("conn-test");

        var registration = new AgentRegistration { Hostname = "" };
        Assert.Throws<HubException>(() => _hub.RegisterAgent(registration));
    }

    [Fact]
    public void RegisterAgent_ThrowsHubException_WhenHostnameIsWhitespace()
    {
        SetupHubContext("conn-test");

        var registration = new AgentRegistration { Hostname = "   " };
        Assert.Throws<HubException>(() => _hub.RegisterAgent(registration));
    }

    [Fact]
    public async Task OnDisconnectedAsync_UnregistersAgent()
    {
        SetupHubContext("conn-test");

        // First register, then disconnect
        _connectionManager.RegisterAgent("conn-test", new AgentRegistration { Hostname = "test-host" });
        Assert.True(_connectionManager.IsAgentConnected("test-host"));

        await _hub.OnDisconnectedAsync(null);

        Assert.False(_connectionManager.IsAgentConnected("test-host"));
    }

    private void SetupHubContext(string connectionId)
    {
        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns(connectionId);
        _hub.Context = mockContext.Object;
    }
}
