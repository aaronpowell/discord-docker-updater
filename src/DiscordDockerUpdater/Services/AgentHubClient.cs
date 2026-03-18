using System.Runtime.InteropServices;
using DiscordDockerUpdater.Configuration;
using DiscordDockerUpdater.Models;
using Docker.DotNet;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace DiscordDockerUpdater.Services;

/// <summary>
/// Background service that runs in agent mode. Connects to the bot's SignalR hub,
/// registers this host, and handles incoming update commands over the persistent connection.
/// </summary>
public class AgentHubClient : BackgroundService
{
    private readonly BotConfiguration _config;
    private readonly ContainerInspector _inspector;
    private readonly DockerComposeExecutor _executor;
    private readonly ComposeFileUpdater _updater;
    private readonly IDockerClient _dockerClient;
    private readonly ILogger<AgentHubClient> _logger;
    private HubConnection? _connection;

    public AgentHubClient(
        IOptions<BotConfiguration> config,
        ContainerInspector inspector,
        DockerComposeExecutor executor,
        ComposeFileUpdater updater,
        IDockerClient dockerClient,
        ILogger<AgentHubClient> logger)
    {
        _config = config.Value;
        _inspector = inspector;
        _executor = executor;
        _updater = updater;
        _dockerClient = dockerClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hubUrl = _config.HubUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(hubUrl))
        {
            _logger.LogCritical("HubUrl is not configured. The agent cannot connect to the bot. Set Bot__HubUrl.");
            return;
        }

        _connection = new HubConnectionBuilder()
            .WithUrl($"{hubUrl}/agent-hub", options =>
            {
                if (!string.IsNullOrWhiteSpace(_config.AgentToken))
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(_config.AgentToken);
                }
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<AgentUpdateRequest, AgentUpdateResponse>("ExecuteUpdate", HandleUpdateAsync);

        _connection.Reconnected += async _ =>
        {
            _logger.LogInformation("Reconnected to bot hub — re-registering agent");
            await RegisterAsync(stoppingToken);
        };

        _connection.Closed += ex =>
        {
            if (ex is not null)
                _logger.LogWarning(ex, "Hub connection closed with error");
            else
                _logger.LogInformation("Hub connection closed");
            return Task.CompletedTask;
        };

        // Connect with retry loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Connecting to bot hub at {HubUrl}", hubUrl);
                await _connection.StartAsync(stoppingToken);
                _logger.LogInformation("Connected to bot hub");

                await RegisterAsync(stoppingToken);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to bot hub — retrying in 5 seconds");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        // Keep alive until shutdown
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var registration = new AgentRegistration
        {
            Hostname = Environment.MachineName,
            OSDescription = RuntimeInformation.OSDescription,
        };

        // Gather container list and Docker version as metadata
        try
        {
            var version = await _dockerClient.System.GetVersionAsync(cancellationToken);
            registration.DockerVersion = version.Version;

            var containers = await _dockerClient.Containers.ListContainersAsync(
                new Docker.DotNet.Models.ContainersListParameters { All = false }, cancellationToken);
            registration.Containers = containers
                .SelectMany(c => c.Names)
                .Select(n => n.TrimStart('/'))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to gather Docker metadata for registration");
        }

        await _connection!.InvokeAsync("RegisterAgent", registration, cancellationToken);
        _logger.LogInformation("Registered as agent with hostname {Hostname}", registration.Hostname);
    }

    /// <summary>
    /// Handles an update command from the bot. Mirrors the logic from the old /agent/update endpoint.
    /// </summary>
    private async Task<AgentUpdateResponse> HandleUpdateAsync(AgentUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ContainerName))
        {
            return new AgentUpdateResponse
            {
                Success = false,
                ErrorOutput = "ContainerName is required"
            };
        }

        _logger.LogInformation("Received update command for container {Container}", request.ContainerName);

        var composeInfo = await _inspector.InspectAsync(request.ContainerName);
        if (composeInfo is null)
        {
            return new AgentUpdateResponse
            {
                Success = false,
                ErrorOutput = $"Could not find compose info for container '{request.ContainerName}'"
            };
        }

        try
        {
            var result = await _executor.UpdateServiceAsync(composeInfo.ConfigFile, composeInfo.ServiceName);

            var sourceUpdated = false;
            if (result.Success && !string.IsNullOrWhiteSpace(request.Digest) && !string.IsNullOrWhiteSpace(request.ImageName))
            {
                var pinnedImage = ComposeFileUpdater.BuildImageWithDigest(request.ImageName, request.Digest);
                sourceUpdated = await _updater.UpdateImageReferenceAsync(
                    composeInfo.ConfigFile, composeInfo.ServiceName, pinnedImage);
            }

            return new AgentUpdateResponse
            {
                Success = result.Success,
                PullOutput = result.PullOutput,
                UpOutput = result.UpOutput,
                ErrorOutput = result.ErrorOutput,
                DurationSeconds = result.Duration.TotalSeconds,
                ServiceName = composeInfo.ServiceName,
                ProjectName = composeInfo.ProjectName,
                ConfigFile = composeInfo.ConfigFile,
                SourceUpdated = sourceUpdated
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update failed for container {Container}", request.ContainerName);
            return new AgentUpdateResponse
            {
                Success = false,
                ErrorOutput = $"Exception: {ex.Message}"
            };
        }
    }
}
