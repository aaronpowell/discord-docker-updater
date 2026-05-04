namespace DiscordDockerUpdater.Configuration;

public class BotConfiguration
{
    public const string SectionName = "Bot";

    public string DiscordToken { get; set; } = "";
    public ulong ChannelId { get; set; }

    /// <summary>
    /// Discord user IDs allowed to invoke commands and click buttons.
    /// Comma- or semicolon-separated, e.g. "1234567890,9876543210".
    /// Whitespace is trimmed; non-numeric entries are ignored.
    /// When empty/null, no user-level restriction is applied (NOT recommended for shared guilds).
    /// </summary>
    public string? AllowedUserIds { get; set; }

    /// <summary>
    /// Discord guild ID where the bot is allowed to operate.
    /// When 0, slash commands are registered globally (NOT recommended for shared guilds).
    /// </summary>
    public ulong GuildId { get; set; }

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
    /// Enables multi-host agent mode for the BOT side. When true (default,
    /// preserving existing upstream behavior), the bot exposes the
    /// /agent-hub SignalR endpoint, registers the AgentConnectionManager,
    /// and surfaces /agents and /agent-info slash commands so a fleet of
    /// remote Docker hosts can connect as agents and route their updates
    /// through this bot. Set to false for single-host deployments — the
    /// hub endpoint isn't mapped and the agent slash commands aren't
    /// registered.
    /// </summary>
    public bool MultiHostMode { get; set; } = true;

    /// <summary>
    /// Name of the Diun container to query for /status's authoritative
    /// tracked-images list. Defaults to "diun" but compose projects
    /// often prefix service names (e.g. "homelab-diun-1") so this is
    /// configurable.
    /// </summary>
    public string DiunContainerName { get; set; } = "diun";

    /// <summary>
    /// Shared token for authenticating agent connections.
    /// In bot mode: validates incoming agent hub connections.
    /// In agent mode: sent as the access token when connecting to the bot's hub.
    /// </summary>
    public string? AgentToken { get; set; }

    /// <summary>
    /// Base URL of the bot's SignalR hub. Only used in agent mode.
    /// Agents connect to {HubUrl}/agent-hub on startup.
    /// Example: "http://192.168.1.100:8080"
    /// </summary>
    public string? HubUrl { get; set; }
}
