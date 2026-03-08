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

    /// <summary>
    /// When true, after a successful container update the compose file's image reference
    /// is updated to pin to the new digest, keeping the source file in sync with what's running.
    /// Default is false.
    /// </summary>
    public bool UpdateSource { get; set; }

    /// <summary>
    /// When true, runs in agent mode — a lightweight HTTP API that receives update commands
    /// from the central bot and executes them locally via Docker socket.
    /// Discord bot features are disabled in agent mode.
    /// </summary>
    public bool AgentMode { get; set; }

    /// <summary>
    /// Token for authenticating agent API requests (both inbound in agent mode and outbound in bot mode).
    /// </summary>
    public string? AgentToken { get; set; }

    /// <summary>
    /// Maps DIUN hostnames to agent URLs for remote update routing.
    /// Key: hostname as reported by DIUN. Value: agent base URL (e.g., "http://192.168.1.101:8080").
    /// The special value "local" means the host is the same machine as the bot (use local Docker socket).
    /// Only used in bot mode.
    /// </summary>
    public Dictionary<string, string> HostRegistry { get; set; } = new();
}
