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
/// Implements the Singleton pattern via dependency injection.
/// </summary>
public class UpdateTracker(ILogger<UpdateTracker> logger)
{
    private readonly ConcurrentDictionary<string, PendingUpdate> _pendingUpdates = new();

    /// <summary>
    /// Adds a new update notification to the tracker.
    /// </summary>
    /// <param name="payload">The Diun payload containing update information</param>
    /// <returns>The created PendingUpdate with a generated ID</returns>
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
            logger.LogInformation(
                "Added pending update {UpdateId} for image {Image}",
                update.Id,
                payload.Image);
            return update;
        }

        // This should be extremely rare (GUID collision), but handle it gracefully
        logger.LogWarning(
            "Failed to add update with ID {UpdateId}, retrying with new ID",
            update.Id);
        
        // Retry with a new ID
        update.Id = Guid.NewGuid().ToString();
        _pendingUpdates.TryAdd(update.Id, update);
        return update;
    }

    /// <summary>
    /// Retrieves a specific update by its ID.
    /// </summary>
    /// <param name="id">The update ID</param>
    /// <returns>The PendingUpdate if found, null otherwise</returns>
    public PendingUpdate? GetUpdate(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        _pendingUpdates.TryGetValue(id, out var update);
        return update;
    }

    /// <summary>
    /// Gets all pending (non-completed) updates.
    /// </summary>
    /// <returns>A collection of pending updates</returns>
    public IEnumerable<PendingUpdate> GetPendingUpdates()
    {
        return _pendingUpdates.Values
            .Where(u => !u.IsCompleted)
            .OrderBy(u => u.ReceivedAt)
            .ToList();
    }

    /// <summary>
    /// Marks an update as completed.
    /// </summary>
    /// <param name="id">The update ID</param>
    /// <returns>True if the update was found and marked as completed, false otherwise</returns>
    public bool MarkCompleted(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        if (_pendingUpdates.TryGetValue(id, out var update))
        {
            update.IsCompleted = true;
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
    /// Retrieves an existing update by image name and digest for idempotency.
    /// </summary>
    /// <param name="image">The image name</param>
    /// <param name="digest">The image digest</param>
    /// <returns>The existing PendingUpdate if found, null otherwise</returns>
    public PendingUpdate? GetByImageAndDigest(string image, string? digest)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        // Find an existing update with the same image and digest
        return _pendingUpdates.Values
            .FirstOrDefault(u => 
                string.Equals(u.Payload.Image, image, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(u.Payload.Digest, digest, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Removes pending updates older than the specified retention period.
    /// </summary>
    /// <param name="retentionDays">Number of days to retain updates</param>
    /// <returns>The number of updates removed</returns>
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
    /// <returns>The number of pending updates</returns>
    public int GetPendingCount()
    {
        return _pendingUpdates.Values.Count(u => !u.IsCompleted);
    }
}
