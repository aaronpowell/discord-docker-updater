using DiscordDockerUpdater.Models;
using DiscordDockerUpdater.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;

namespace DiscordDockerUpdater.Tests.Services;

public class UpdateStoreTests : IDisposable
{
    private readonly UpdateStore _store;
    private readonly string _dbPath;

    public UpdateStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test-store-{Guid.NewGuid()}.db");
        _store = new UpdateStore(Mock.Of<ILogger<UpdateStore>>(), _dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Save_And_LoadAll_RoundTrips()
    {
        var update = new PendingUpdate
        {
            Id = "test-1",
            Payload = new DiunPayload { Image = "nginx:latest", Hostname = "server1" },
            ReceivedAt = DateTime.UtcNow
        };

        _store.Save(update);
        var loaded = _store.LoadAll();

        Assert.Single(loaded);
        Assert.Equal("test-1", loaded[0].Id);
        Assert.Equal("nginx:latest", loaded[0].Payload.Image);
        Assert.Equal("server1", loaded[0].Payload.Hostname);
        Assert.False(loaded[0].IsCompleted);
    }

    [Fact]
    public void MarkCompleted_PersistsCompletionStatus()
    {
        var update = new PendingUpdate
        {
            Id = "test-1",
            Payload = new DiunPayload { Image = "nginx:latest" },
            ReceivedAt = DateTime.UtcNow
        };

        _store.Save(update);
        _store.MarkCompleted("test-1");

        var loaded = _store.LoadAll();
        Assert.Single(loaded);
        Assert.True(loaded[0].IsCompleted);
    }

    [Fact]
    public void SetDiscordMessageId_PersistsMessageId()
    {
        var update = new PendingUpdate
        {
            Id = "test-1",
            Payload = new DiunPayload { Image = "nginx:latest" },
            ReceivedAt = DateTime.UtcNow
        };

        _store.Save(update);
        _store.SetDiscordMessageId("test-1", 123456789UL);

        var loaded = _store.LoadAll();
        Assert.Single(loaded);
        Assert.Equal(123456789UL, loaded[0].DiscordMessageId);
    }

    [Fact]
    public void RemoveStale_RemovesOldPendingUpdates()
    {
        var oldUpdate = new PendingUpdate
        {
            Id = "old-1",
            Payload = new DiunPayload { Image = "old:latest" },
            ReceivedAt = DateTime.UtcNow.AddDays(-10)
        };
        var recentUpdate = new PendingUpdate
        {
            Id = "recent-1",
            Payload = new DiunPayload { Image = "recent:latest" },
            ReceivedAt = DateTime.UtcNow
        };

        _store.Save(oldUpdate);
        _store.Save(recentUpdate);

        var removed = _store.RemoveStale(7);
        Assert.Equal(1, removed);

        var loaded = _store.LoadAll();
        Assert.Single(loaded);
        Assert.Equal("recent-1", loaded[0].Id);
    }

    [Fact]
    public void LoadAll_PreservesPayloadMetadata()
    {
        var update = new PendingUpdate
        {
            Id = "test-1",
            Payload = new DiunPayload
            {
                Image = "nginx:latest",
                Hostname = "myhost",
                Digest = "sha256:abc123",
                Status = "new",
                Metadata = new DiunMetadata
                {
                    CtnNames = "/nginx",
                    CtnState = "running"
                }
            },
            ReceivedAt = DateTime.UtcNow
        };

        _store.Save(update);
        var loaded = _store.LoadAll();

        Assert.Single(loaded);
        Assert.Equal("sha256:abc123", loaded[0].Payload.Digest);
        Assert.Equal("myhost", loaded[0].Payload.Hostname);
        Assert.Equal("/nginx", loaded[0].Payload.Metadata?.CtnNames);
        Assert.Equal("running", loaded[0].Payload.Metadata?.CtnState);
    }

    [Fact]
    public void Persistence_SurvivesReopeningSameDatabase()
    {
        var update = new PendingUpdate
        {
            Id = "persist-test",
            Payload = new DiunPayload { Image = "test:latest", Hostname = "host1" },
            ReceivedAt = DateTime.UtcNow
        };

        _store.Save(update);
        _store.Dispose();
        SqliteConnection.ClearAllPools();

        // Re-open the same database
        using var store2 = new UpdateStore(Mock.Of<ILogger<UpdateStore>>(), _dbPath);
        var loaded = store2.LoadAll();

        Assert.Single(loaded);
        Assert.Equal("persist-test", loaded[0].Id);
        Assert.Equal("test:latest", loaded[0].Payload.Image);
    }
}
