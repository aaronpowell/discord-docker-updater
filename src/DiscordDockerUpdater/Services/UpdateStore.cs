using System.Text.Json;
using DiscordDockerUpdater.Models;
using Microsoft.Data.Sqlite;

namespace DiscordDockerUpdater.Services;

/// <summary>
/// SQLite-backed persistence for pending updates. Survives process restarts.
/// The in-memory UpdateTracker delegates to this store for durable operations.
/// </summary>
public class UpdateStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<UpdateStore> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public UpdateStore(ILogger<UpdateStore> logger, string? dbPath = null)
    {
        _logger = logger;
        var path = dbPath ?? Path.Combine(AppContext.BaseDirectory, "updates.db");
        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS pending_updates (
                id TEXT PRIMARY KEY,
                payload_json TEXT NOT NULL,
                received_at TEXT NOT NULL,
                discord_message_id INTEGER,
                is_completed INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_pending_updates_completed 
                ON pending_updates(is_completed);
            CREATE INDEX IF NOT EXISTS idx_pending_updates_image_digest 
                ON pending_updates(
                    json_extract(payload_json, '$.image'),
                    json_extract(payload_json, '$.digest')
                );
            """;
        cmd.ExecuteNonQuery();
        _logger.LogInformation("Update store initialized");
    }

    /// <summary>
    /// Persists a new pending update to the database.
    /// </summary>
    public void Save(PendingUpdate update)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO pending_updates (id, payload_json, received_at, discord_message_id, is_completed)
            VALUES (@id, @payload, @received, @msgId, @completed)
            """;
        cmd.Parameters.AddWithValue("@id", update.Id);
        cmd.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(update.Payload, JsonOptions));
        cmd.Parameters.AddWithValue("@received", update.ReceivedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@msgId", update.DiscordMessageId.HasValue ? (object)update.DiscordMessageId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@completed", update.IsCompleted ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Marks an update as completed in the database.
    /// </summary>
    public void MarkCompleted(string id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE pending_updates SET is_completed = 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Updates the Discord message ID for an update.
    /// </summary>
    public void SetDiscordMessageId(string id, ulong messageId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE pending_updates SET discord_message_id = @msgId WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@msgId", (long)messageId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes updates older than the retention period.
    /// </summary>
    public int RemoveStale(int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("O");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_updates WHERE is_completed = 0 AND received_at < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Loads all updates from the database (for hydrating the in-memory tracker on startup).
    /// </summary>
    public List<PendingUpdate> LoadAll()
    {
        var updates = new List<PendingUpdate>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, payload_json, received_at, discord_message_id, is_completed FROM pending_updates";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var payload = JsonSerializer.Deserialize<DiunPayload>(reader.GetString(1), JsonOptions);
            if (payload is null) continue;

            updates.Add(new PendingUpdate
            {
                Id = reader.GetString(0),
                Payload = payload,
                ReceivedAt = DateTime.Parse(reader.GetString(2)).ToUniversalTime(),
                DiscordMessageId = reader.IsDBNull(3) ? null : (ulong)reader.GetInt64(3),
                IsCompleted = reader.GetInt32(4) == 1
            });
        }

        _logger.LogInformation("Loaded {Count} updates from store", updates.Count);
        return updates;
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
