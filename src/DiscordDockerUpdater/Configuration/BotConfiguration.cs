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
    /// When true, runs in agent mode — connects to the bot's SignalR hub to receive
    /// update commands and executes them locally via Docker socket.
    /// Discord bot features are disabled in agent mode.
    /// </summary>
    public bool AgentMode { get; set; }

    /// <summary>
    /// Shared token for authenticating agent connections.
    /// In bot mode: validates incoming agent hub connections.
    /// In agent mode: sent as the access token when connecting to the bot's hub.
    /// </summary>
    public string? AgentToken { get; set; }

    /// <summary>
    /// Optional friendly name for this agent instance. When set, this name is shown in Discord
    /// messages and accepted in command lookups instead of the machine hostname.
    /// Only applies when running in agent mode.
    /// Example: "home-server" or "prod-box"
    /// </summary>
    public string? FriendlyName { get; set; }

    /// <summary>
    /// Base URL of the bot's SignalR hub. Only used in agent mode.
    /// Agents connect to {HubUrl}/agent-hub on startup.
    /// Example: "http://192.168.1.100:8080"
    /// </summary>
    public string? HubUrl { get; set; }
}
