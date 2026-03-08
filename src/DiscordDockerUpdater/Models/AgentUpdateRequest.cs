namespace DiscordDockerUpdater.Models;

/// <summary>
/// Request sent from the central bot to an agent to trigger a container update.
/// </summary>
public class AgentUpdateRequest
{
    public string ContainerName { get; set; } = "";
    public string ImageName { get; set; } = "";
    public string? Digest { get; set; }
    public string UpdateId { get; set; } = "";
}

/// <summary>
/// Response from an agent after attempting a container update.
/// </summary>
public class AgentUpdateResponse
{
    public bool Success { get; set; }
    public string? PullOutput { get; set; }
    public string? UpOutput { get; set; }
    public string? ErrorOutput { get; set; }
    public double DurationSeconds { get; set; }
    public string? ServiceName { get; set; }
    public string? ProjectName { get; set; }
    public string? ConfigFile { get; set; }
    public bool SourceUpdated { get; set; }
}
