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

    /// <summary>
    /// Optional URL to a logo image to use in Discord embeds.
    /// This should be a publicly accessible URL (e.g., hosted on GitHub, CDN, etc.)
    /// or empty/null to not use a logo.
    /// </summary>
    public string? LogoUrl { get; set; }

    /// <summary>
    /// Token required on incoming webhook requests for basic authentication.
    /// When set, requests must include an Authorization header with "Bearer {token}".
    /// If empty/null, webhook authentication is disabled (not recommended for production).
    /// </summary>
    public string? WebhookToken { get; set; }
}
