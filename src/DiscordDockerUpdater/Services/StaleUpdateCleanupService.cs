using DiscordDockerUpdater.Configuration;
using Microsoft.Extensions.Options;

namespace DiscordDockerUpdater.Services;

/// <summary>
/// Background service that periodically removes stale pending updates.
/// Implements IHostedService for integration with the ASP.NET Core hosting model.
/// </summary>
public class StaleUpdateCleanupService(
    UpdateTracker updateTracker,
    IOptions<BotConfiguration> config,
    ILogger<StaleUpdateCleanupService> logger) : BackgroundService
{
    private readonly BotConfiguration _config = config.Value;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Stale update cleanup service started. Cleanup interval: {Interval}, Retention: {RetentionDays} days",
            _cleanupInterval,
            _config.StaleUpdateRetentionDays);

        // Wait a bit before first cleanup to allow the app to start
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                logger.LogDebug("Running stale update cleanup");
                
                var removedCount = updateTracker.RemoveStaleUpdates(_config.StaleUpdateRetentionDays);
                
                if (removedCount > 0)
                {
                    logger.LogInformation("Stale update cleanup completed. Removed {Count} updates", removedCount);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during stale update cleanup");
            }

            // Wait for the next cleanup cycle
            await Task.Delay(_cleanupInterval, stoppingToken);
        }

        logger.LogInformation("Stale update cleanup service stopped");
    }
}
