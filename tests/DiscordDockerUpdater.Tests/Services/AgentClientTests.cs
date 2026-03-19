using DiscordDockerUpdater.Hubs;
using DiscordDockerUpdater.Models;
using DiscordDockerUpdater.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace DiscordDockerUpdater.Tests.Services;

public class AgentClientTests
{
    private readonly Mock<IHubContext<AgentHub>> _hubContextMock;
    private readonly AgentConnectionManager _connectionManager;
    private readonly AgentClient _agentClient;

    public AgentClientTests()
    {
        _hubContextMock = new Mock<IHubContext<AgentHub>>();
        _connectionManager = new AgentConnectionManager(Mock.Of<ILogger<AgentConnectionManager>>());
        var logger = Mock.Of<ILogger<AgentClient>>();
        _agentClient = new AgentClient(_hubContextMock.Object, _connectionManager, logger);
    }

    [Fact]
    public void IsAgentConnected_ReturnsFalse_WhenHostnameIsNull()
    {
        Assert.False(_agentClient.IsAgentConnected(null));
    }

    [Fact]
    public void IsAgentConnected_ReturnsFalse_WhenHostnameIsEmpty()
    {
        Assert.False(_agentClient.IsAgentConnected(""));
        Assert.False(_agentClient.IsAgentConnected("  "));
    }

    [Fact]
    public void IsAgentConnected_ReturnsFalse_WhenNoAgentRegistered()
    {
        Assert.False(_agentClient.IsAgentConnected("unknown-host"));
    }

    [Fact]
    public void IsAgentConnected_ReturnsTrue_WhenAgentRegistered()
    {
        _connectionManager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "server1" });

        Assert.True(_agentClient.IsAgentConnected("server1"));
    }

    [Fact]
    public async Task SendUpdateAsync_ThrowsInvalidOperation_WhenNoAgentConnected()
    {
        var request = new AgentUpdateRequest
        {
            ContainerName = "test",
            ImageName = "test:latest",
            UpdateId = "upd-1"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _agentClient.SendUpdateAsync("unknown-host", request));
    }

    [Fact]
    public async Task SendUpdateAsync_InvokesHubMethod_WhenAgentConnected()
    {
        _connectionManager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "server1" });

        var expectedResponse = new AgentUpdateResponse
        {
            Success = true,
            ServiceName = "myservice",
            DurationSeconds = 5.0
        };

        var mockClientProxy = new Mock<ISingleClientProxy>();
        mockClientProxy
            .Setup(x => x.InvokeCoreAsync<AgentUpdateResponse>(
                "ExecuteUpdate",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var mockClients = new Mock<IHubClients>();
        mockClients.Setup(x => x.Client("conn-1")).Returns(mockClientProxy.Object);
        _hubContextMock.Setup(x => x.Clients).Returns(mockClients.Object);

        var request = new AgentUpdateRequest
        {
            ContainerName = "mycontainer",
            ImageName = "myimage:latest",
            UpdateId = "upd-1"
        };

        var result = await _agentClient.SendUpdateAsync("server1", request);

        Assert.True(result.Success);
        Assert.Equal("myservice", result.ServiceName);
        Assert.Equal(5.0, result.DurationSeconds);
    }

    [Fact]
    public async Task SendUpdateAsync_ReturnsErrorResponse_WhenAgentReturnsNull()
    {
        _connectionManager.RegisterAgent("conn-1", new AgentRegistration { Hostname = "server1" });

        var mockClientProxy = new Mock<ISingleClientProxy>();
        mockClientProxy
            .Setup(x => x.InvokeCoreAsync<AgentUpdateResponse>(
                "ExecuteUpdate",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentUpdateResponse?)null!);

        var mockClients = new Mock<IHubClients>();
        mockClients.Setup(x => x.Client("conn-1")).Returns(mockClientProxy.Object);
        _hubContextMock.Setup(x => x.Clients).Returns(mockClients.Object);

        var request = new AgentUpdateRequest
        {
            ContainerName = "test",
            ImageName = "test:latest",
            UpdateId = "upd-1"
        };

        var result = await _agentClient.SendUpdateAsync("server1", request);

        Assert.False(result.Success);
        Assert.Contains("Empty response", result.ErrorOutput);
    }
}
