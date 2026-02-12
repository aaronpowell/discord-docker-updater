using DiscordDockerUpdater.Models;
using DiscordDockerUpdater.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace DiscordDockerUpdater.Tests.Services;

public class UpdateTrackerTests
{
    private readonly UpdateTracker _tracker;
    private readonly Mock<ILogger<UpdateTracker>> _mockLogger;

    public UpdateTrackerTests()
    {
        _mockLogger = new Mock<ILogger<UpdateTracker>>();
        _tracker = new UpdateTracker(_mockLogger.Object);
    }

    [Fact]
    public void AddUpdate_StoresPayload_ReturnsUpdateWithId()
    {
        // Arrange
        var payload = new DiunPayload
        {
            Image = "nginx:latest",
            Status = "update",
            Hostname = "test-host"
        };

        // Act
        var result = _tracker.AddUpdate(payload);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Id);
        Assert.NotEqual(Guid.Empty.ToString(), result.Id);
        Assert.Same(payload, result.Payload);
        Assert.False(result.IsCompleted);
        Assert.Null(result.DiscordMessageId);
        Assert.True((DateTime.UtcNow - result.ReceivedAt).TotalSeconds < 5);
    }

    [Fact]
    public void AddUpdate_WithNullPayload_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _tracker.AddUpdate(null!));
    }

    [Fact]
    public void GetUpdate_WithValidId_RetrievesCorrectUpdate()
    {
        // Arrange
        var payload = new DiunPayload
        {
            Image = "postgres:15",
            Status = "new"
        };
        var addedUpdate = _tracker.AddUpdate(payload);

        // Act
        var retrievedUpdate = _tracker.GetUpdate(addedUpdate.Id);

        // Assert
        Assert.NotNull(retrievedUpdate);
        Assert.Equal(addedUpdate.Id, retrievedUpdate.Id);
        Assert.Same(addedUpdate.Payload, retrievedUpdate.Payload);
    }

    [Fact]
    public void GetUpdate_WithInvalidId_ReturnsNull()
    {
        // Act
        var result = _tracker.GetUpdate("non-existent-id");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetUpdate_WithNullOrEmptyId_ReturnsNull()
    {
        // Act & Assert
        Assert.Null(_tracker.GetUpdate(null!));
        Assert.Null(_tracker.GetUpdate(string.Empty));
        Assert.Null(_tracker.GetUpdate("   "));
    }

    [Fact]
    public void GetPendingUpdates_ReturnsOnlyNonCompletedUpdates()
    {
        // Arrange
        var payload1 = new DiunPayload { Image = "redis:7" };
        var payload2 = new DiunPayload { Image = "mongo:6" };
        var payload3 = new DiunPayload { Image = "mysql:8" };

        var update1 = _tracker.AddUpdate(payload1);
        var update2 = _tracker.AddUpdate(payload2);
        var update3 = _tracker.AddUpdate(payload3);

        // Mark one as completed
        _tracker.MarkCompleted(update2.Id);

        // Act
        var pendingUpdates = _tracker.GetPendingUpdates().ToList();

        // Assert
        Assert.Equal(2, pendingUpdates.Count);
        Assert.Contains(pendingUpdates, u => u.Id == update1.Id);
        Assert.Contains(pendingUpdates, u => u.Id == update3.Id);
        Assert.DoesNotContain(pendingUpdates, u => u.Id == update2.Id);
    }

    [Fact]
    public void GetPendingUpdates_OrdersByReceivedAt()
    {
        // Arrange
        var payload1 = new DiunPayload { Image = "image1" };
        var payload2 = new DiunPayload { Image = "image2" };
        var payload3 = new DiunPayload { Image = "image3" };

        var update1 = _tracker.AddUpdate(payload1);
        Thread.Sleep(10); // Ensure different timestamps
        var update2 = _tracker.AddUpdate(payload2);
        Thread.Sleep(10);
        var update3 = _tracker.AddUpdate(payload3);

        // Act
        var pendingUpdates = _tracker.GetPendingUpdates().ToList();

        // Assert
        Assert.Equal(3, pendingUpdates.Count);
        Assert.Equal(update1.Id, pendingUpdates[0].Id);
        Assert.Equal(update2.Id, pendingUpdates[1].Id);
        Assert.Equal(update3.Id, pendingUpdates[2].Id);
    }

    [Fact]
    public void GetPendingUpdates_WhenEmpty_ReturnsEmptyCollection()
    {
        // Act
        var result = _tracker.GetPendingUpdates();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void MarkCompleted_WithValidId_MarksAsCompleted()
    {
        // Arrange
        var payload = new DiunPayload { Image = "alpine:latest" };
        var update = _tracker.AddUpdate(payload);

        // Act
        var result = _tracker.MarkCompleted(update.Id);

        // Assert
        Assert.True(result);
        var retrievedUpdate = _tracker.GetUpdate(update.Id);
        Assert.NotNull(retrievedUpdate);
        Assert.True(retrievedUpdate.IsCompleted);

        // Verify it's not in pending updates
        var pendingUpdates = _tracker.GetPendingUpdates();
        Assert.DoesNotContain(pendingUpdates, u => u.Id == update.Id);
    }

    [Fact]
    public void MarkCompleted_WithInvalidId_ReturnsFalse()
    {
        // Act
        var result = _tracker.MarkCompleted("non-existent-id");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void MarkCompleted_WithNullOrEmptyId_ReturnsFalse()
    {
        // Act & Assert
        Assert.False(_tracker.MarkCompleted(null!));
        Assert.False(_tracker.MarkCompleted(string.Empty));
        Assert.False(_tracker.MarkCompleted("   "));
    }

    [Fact]
    public void UpdateTracker_IsThreadSafe_HandlesMultipleSimultaneousAdds()
    {
        // Arrange
        const int threadCount = 10;
        const int updatesPerThread = 100;
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < threadCount; i++)
        {
            var threadId = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < updatesPerThread; j++)
                {
                    var payload = new DiunPayload
                    {
                        Image = $"test-image-{threadId}-{j}:latest"
                    };
                    _tracker.AddUpdate(payload);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert
        var allUpdates = _tracker.GetPendingUpdates().ToList();
        Assert.Equal(threadCount * updatesPerThread, allUpdates.Count);
    }

    [Fact]
    public void AddUpdate_GeneratesUniqueIds()
    {
        // Arrange & Act
        var ids = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
        {
            var payload = new DiunPayload { Image = $"image{i}" };
            var update = _tracker.AddUpdate(payload);
            ids.Add(update.Id);
        }

        // Assert
        Assert.Equal(1000, ids.Count); // All IDs should be unique
    }
}
