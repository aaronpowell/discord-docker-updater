using System.Text.Json.Serialization;

namespace DiscordDockerUpdater.Models;

public class DiunPayload
{
    [JsonPropertyName("diun_version")]
    public string? DiunVersion { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("hub_link")]
    public string? HubLink { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("created")]
    public DateTime? Created { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("metadata")]
    public DiunMetadata? Metadata { get; set; }
}

public class DiunMetadata
{
    [JsonPropertyName("ctn_command")]
    public string? CtnCommand { get; set; }

    [JsonPropertyName("ctn_createdat")]
    public string? CtnCreatedAt { get; set; }

    [JsonPropertyName("ctn_id")]
    public string? CtnId { get; set; }

    [JsonPropertyName("ctn_names")]
    public string? CtnNames { get; set; }

    [JsonPropertyName("ctn_size")]
    public string? CtnSize { get; set; }

    [JsonPropertyName("ctn_state")]
    public string? CtnState { get; set; }

    [JsonPropertyName("ctn_status")]
    public string? CtnStatus { get; set; }
}
