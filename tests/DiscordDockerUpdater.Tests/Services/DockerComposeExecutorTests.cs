using DiscordDockerUpdater.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace DiscordDockerUpdater.Tests.Services;

/// <summary>
/// Unit tests for DockerComposeExecutor.
/// Tests input validation and result model behavior.
/// Note: Actual process execution is integration-tested, not unit-tested.
/// </summary>
public class DockerComposeExecutorTests
{
    private readonly Mock<ILogger<DockerComposeExecutor>> _mockLogger;
    private readonly DockerComposeExecutor _executor;

    public DockerComposeExecutorTests()
    {
        _mockLogger = new Mock<ILogger<DockerComposeExecutor>>();
        _executor = new DockerComposeExecutor(_mockLogger.Object);
    }

    [Fact]
    public async Task UpdateServiceAsync_WithNullComposePath_ThrowsArgumentException()
    {
        // Arrange
        string? composePath = null;
        var serviceName = "test-service";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _executor.UpdateServiceAsync(composePath!, serviceName));
        
        Assert.Contains("Compose file path", exception.Message);
    }

    [Fact]
    public async Task UpdateServiceAsync_WithEmptyComposePath_ThrowsArgumentException()
    {
        // Arrange
        var composePath = "";
        var serviceName = "test-service";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _executor.UpdateServiceAsync(composePath, serviceName));
        
        Assert.Contains("Compose file path", exception.Message);
    }

    [Fact]
    public async Task UpdateServiceAsync_WithWhitespaceComposePath_ThrowsArgumentException()
    {
        // Arrange
        var composePath = "   ";
        var serviceName = "test-service";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _executor.UpdateServiceAsync(composePath, serviceName));
        
        Assert.Contains("Compose file path", exception.Message);
    }

    [Fact]
    public async Task UpdateServiceAsync_WithNullServiceName_ThrowsArgumentException()
    {
        // Arrange
        var composePath = "D:\\test\\docker-compose.yml";
        string? serviceName = null;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _executor.UpdateServiceAsync(composePath, serviceName!));
        
        Assert.Contains("Service name", exception.Message);
    }

    [Fact]
    public async Task UpdateServiceAsync_WithEmptyServiceName_ThrowsArgumentException()
    {
        // Arrange
        var composePath = "D:\\test\\docker-compose.yml";
        var serviceName = "";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _executor.UpdateServiceAsync(composePath, serviceName));
        
        Assert.Contains("Service name", exception.Message);
    }

    [Fact]
    public async Task UpdateServiceAsync_WithWhitespaceServiceName_ThrowsArgumentException()
    {
        // Arrange
        var composePath = "D:\\test\\docker-compose.yml";
        var serviceName = "   ";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _executor.UpdateServiceAsync(composePath, serviceName));
        
        Assert.Contains("Service name", exception.Message);
    }

    [Fact]
    public async Task UpdateServiceAsync_WithNonExistentComposePath_ThrowsFileNotFoundException()
    {
        // Arrange
        var composePath = "D:\\nonexistent\\docker-compose.yml";
        var serviceName = "test-service";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => _executor.UpdateServiceAsync(composePath, serviceName));
        
        Assert.Contains("Compose file not found", exception.Message);
        Assert.Contains(composePath, exception.Message);
    }

    [Fact]
    public void ComposeExecutionResult_DefaultConstructor_InitializesProperties()
    {
        // Arrange & Act
        var result = new ComposeExecutionResult();

        // Assert
        Assert.False(result.Success);
        Assert.Equal("", result.PullOutput);
        Assert.Equal("", result.UpOutput);
        Assert.Equal("", result.ErrorOutput);
        Assert.Equal(TimeSpan.Zero, result.Duration);
    }

    [Fact]
    public void ComposeExecutionResult_CanSetAllProperties()
    {
        // Arrange
        var result = new ComposeExecutionResult();

        // Act
        result.Success = true;
        result.PullOutput = "Pull output";
        result.UpOutput = "Up output";
        result.ErrorOutput = "Error output";
        result.Duration = TimeSpan.FromSeconds(42);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Pull output", result.PullOutput);
        Assert.Equal("Up output", result.UpOutput);
        Assert.Equal("Error output", result.ErrorOutput);
        Assert.Equal(TimeSpan.FromSeconds(42), result.Duration);
    }

    [Fact]
    public void ComposeExecutionResult_Success_CanBeSetToFalse()
    {
        // Arrange
        var result = new ComposeExecutionResult { Success = true };

        // Act
        result.Success = false;

        // Assert
        Assert.False(result.Success);
    }

    [Fact]
    public void ComposeExecutionResult_OutputFields_CanBeEmptyStrings()
    {
        // Arrange & Act
        var result = new ComposeExecutionResult
        {
            PullOutput = "",
            UpOutput = "",
            ErrorOutput = ""
        };

        // Assert
        Assert.Equal("", result.PullOutput);
        Assert.Equal("", result.UpOutput);
        Assert.Equal("", result.ErrorOutput);
    }
}
