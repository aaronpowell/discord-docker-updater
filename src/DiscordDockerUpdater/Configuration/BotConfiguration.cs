namespace DiscordDockerUpdater.Configuration;

public class BotConfiguration
{
    public const string SectionName = "Bot";
    
    public string DiscordToken { get; set; } = "";
    public ulong ChannelId { get; set; }
    
    /// <summary>
    /// Number of days to retain pending updates before cleaning them up.
    /// Default is 7 days. Updates older than this will be removed by the StaleUpdateCleanupService.
    /// </summary>
    public int StaleUpdateRetentionDays { get; set; } = 7;
}
