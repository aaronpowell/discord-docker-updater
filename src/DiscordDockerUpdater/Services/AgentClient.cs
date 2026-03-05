using System.Net.Http.Json;
using DiscordDockerUpdater.Configuration;
using DiscordDockerUpdater.Models;
using Microsoft.Extensions.Options;

namespace DiscordDockerUpdater.Services;

/// <summary>
/// HTTP client for communicating with remote Discord Docker Updater agents.
/// Used by the bot to proxy update commands to the correct host.
/// </summary>
public class AgentClient(HttpClient httpClient, IOptions<BotConfiguration> config, ILogger<AgentClient> logger)
{
    private readonly BotConfiguration _config = config.Value;

    /// <summary>
    /// Resolves the DIUN hostname to an agent URL from the host registry.
    /// Returns null if the hostname is not registered or is "local".
    /// </summary>
    public string? ResolveAgentUrl(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return null;

        if (_config.HostRegistry.TryGetValue(hostname, out var url))
        {
            if (string.Equals(url, "local", StringComparison.OrdinalIgnoreCase))
                return null; // Local host — no agent needed
            return url;
        }

        // No entry — default to local
        return null;
    }

    /// <summary>
    /// Sends an update command to a remote agent and returns the result.
    /// </summary>
    public async Task<AgentUpdateResponse> SendUpdateAsync(string agentBaseUrl, AgentUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var url = $"{agentBaseUrl.TrimEnd('/')}/agent/update";
        logger.LogInformation("Sending update command to agent at {Url} for container {Container}", url, request.ContainerName);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Content = JsonContent.Create(request);

        if (!string.IsNullOrWhiteSpace(_config.AgentToken))
        {
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.AgentToken);
        }

        var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AgentUpdateResponse>(cancellationToken);
        return result ?? new AgentUpdateResponse { Success = false, ErrorOutput = "Empty response from agent" };
    }
}
