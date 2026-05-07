using System.Collections.Concurrent;
using DiscordDockerUpdater.Models;

namespace DiscordDockerUpdater.Services;

public class PendingUpdate
{
    public required string Id { get; set; }  // GUID
    public required DiunPayload Payload { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public ulong? DiscordMessageId { get; set; }  // set after Discord message is posted
    public bool IsCompleted { get; set; }
}

/// <summary>
/// Thread-safe service for tracking pending Docker image updates.
/// Backed by SQLite via <see cref="UpdateStore"/> for cross-restart persistence.
/// </summary>
public class UpdateTracker(ILogger<UpdateTracker> logger, UpdateStore store)
{
    private readonly ConcurrentDictionary<string, PendingUpdate> _pendingUpdates = new(
        store.LoadAll().ToDictionary(u => u.Id));

    /// <summary>
    /// Adds a new update notification to the tracker.
    /// </summary>
    public PendingUpdate AddUpdate(DiunPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var update = new PendingUpdate
        {
            Id = Guid.NewGuid().ToString(),
            Payload = payload,
            ReceivedAt = DateTime.UtcNow
        };

        if (_pendingUpdates.TryAdd(update.Id, update))
        {
            store.Save(update);
            logger.LogInformation(
                "Added pending update {UpdateId} for image {Image}",
                update.Id,
                payload.Image);
            return update;
        }

        // GUID collision — retry
        logger.LogWarning("Failed to add update with ID {UpdateId}, retrying with new ID", update.Id);
        update.Id = Guid.NewGuid().ToString();
        _pendingUpdates.TryAdd(update.Id, update);
        store.Save(update);
        return update;
    }

    /// <summary>
    /// Retrieves a specific update by its ID.
    /// </summary>
    public PendingUpdate? GetUpdate(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        _pendingUpdates.TryGetValue(id, out var update);
        return update;
    }

    /// <summary>
    /// Gets all pending (non-completed) updates.
    /// </summary>
    public IEnumerable<PendingUpdate> GetPendingUpdates()
    {
        return _pendingUpdates.Values
            .Where(u => !u.IsCompleted)
            .OrderBy(u => u.ReceivedAt)
            .ToList();
    }

    /// <summary>
    /// Gets pending updates filtered by hostname.
    /// </summary>
    public IEnumerable<PendingUpdate> GetPendingUpdatesForHost(string hostname)
    {
        return _pendingUpdates.Values
            .Where(u => !u.IsCompleted &&
                string.Equals(u.Payload.Hostname, hostname, StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.ReceivedAt)
            .ToList();
    }

    /// <summary>
    /// Marks an update as completed.
    /// </summary>
    public bool MarkCompleted(string id)
    {
        if (string.IsNullOrEmpty(id))
            return false;

        if (_pendingUpdates.TryGetValue(id, out var update))
        {
            update.IsCompleted = true;
            store.MarkCompleted(id);
            logger.LogInformation(
                "Marked update {UpdateId} for image {Image} as completed",
                id,
                update.Payload.Image);
            return true;
        }

        logger.LogWarning("Attempted to mark non-existent update {UpdateId} as completed", id);
        return false;
    }

    /// <summary>
    /// Sets the Discord message ID after the notification is posted.
    /// </summary>
    public void SetDiscordMessageId(string id, ulong messageId)
    {
        if (_pendingUpdates.TryGetValue(id, out var update))
        {
            update.DiscordMessageId = messageId;
            store.SetDiscordMessageId(id, messageId);
        }
    }

    /// <summary>
    /// Retrieves an existing update by image name and digest for idempotency.
    /// </summary>
    public PendingUpdate? GetByImageAndDigest(string image, string? digest)
    {
        if (string.IsNullOrWhiteSpace(image))
            return null;

        return _pendingUpdates.Values
            .FirstOrDefault(u =>
                string.Equals(u.Payload.Image, image, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(u.Payload.Digest, digest, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns all non-completed updates for the given image (any digest). Used to
    /// supersede stale notifications when a fresher one arrives so the channel
    /// doesn't accumulate multiple cards for the same image.
    /// </summary>
    public IEnumerable<PendingUpdate> GetPendingByImage(string image)
    {
        if (string.IsNullOrWhiteSpace(image))
            return Array.Empty<PendingUpdate>();

        return _pendingUpdates.Values
            .Where(u => !u.IsCompleted &&
                string.Equals(u.Payload.Image, image, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Removes pending updates older than the specified retention period.
    /// </summary>
    public int RemoveStaleUpdates(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            logger.LogWarning("Invalid retention period {RetentionDays}, skipping cleanup", retentionDays);
            return 0;
        }

        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
        var staleUpdates = _pendingUpdates.Values
            .Where(u => !u.IsCompleted && u.ReceivedAt < cutoffDate)
            .ToList();

        var removedCount = 0;
        foreach (var update in staleUpdates)
        {
            if (_pendingUpdates.TryRemove(update.Id, out _))
            {
                removedCount++;
                logger.LogInformation(
                    "Removed stale update {UpdateId} for image {Image} (received at {ReceivedAt})",
                    update.Id,
                    update.Payload.Image,
                    update.ReceivedAt);
            }
        }

        store.RemoveStale(retentionDays);

        if (removedCount > 0)
        {
            logger.LogInformation(
                "Removed {Count} stale updates older than {RetentionDays} days",
                removedCount,
                retentionDays);
        }

        return removedCount;
    }

    /// <summary>
    /// Gets the total count of pending (non-completed) updates.
    /// </summary>
    public int GetPendingCount()
    {
        return _pendingUpdates.Values.Count(u => !u.IsCompleted);
    }
}
