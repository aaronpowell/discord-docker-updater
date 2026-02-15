using System.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace DiscordDockerUpdater.Services;

/// <summary>
/// Executes Docker container updates (pull and recreate) using the Docker.DotNet SDK.
/// Replaces the previous process-based Docker Compose approach to avoid CLI version mismatches
/// on platforms like Synology NAS.
/// </summary>
public class DockerComposeExecutor(IDockerClient dockerClient, ILogger<DockerComposeExecutor> logger)
{

    /// <summary>
    /// Pulls the latest image and recreates the container for a given service.
    /// Uses the Docker.DotNet SDK to communicate directly via the Docker socket.
    /// </summary>
    /// <param name="composeFilePath">Absolute path to the docker-compose.yml (used for logging/context)</param>
    /// <param name="serviceName">The service name within the compose file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result with success/failure, stdout, stderr</returns>
    public async Task<ComposeExecutionResult> UpdateServiceAsync(
        string composeFilePath, 
        string serviceName, 
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(composeFilePath))
        {
            throw new ArgumentException("Compose file path cannot be null or empty", nameof(composeFilePath));
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));
        }

        logger.LogInformation(
            "Starting Docker update for service '{ServiceName}' using compose file '{ComposePath}'",
            serviceName, 
            composeFilePath);

        var stopwatch = Stopwatch.StartNew();
        var result = new ComposeExecutionResult();

        try
        {
            // Find the container for this compose service
            var container = await FindComposeContainerAsync(composeFilePath, serviceName, cancellationToken);
            if (container == null)
            {
                result.Success = false;
                result.ErrorOutput = $"No container found for compose service '{serviceName}'";
                logger.LogError("No container found for compose service '{ServiceName}'", serviceName);
                return result;
            }

            var containerId = container.ID;
            var containerName = container.Names.FirstOrDefault()?.TrimStart('/') ?? containerId[..12];

            // Inspect the container to get its full configuration
            var inspection = await dockerClient.Containers.InspectContainerAsync(containerId, cancellationToken);
            var currentImage = inspection.Config.Image;

            // Step 1: Pull the latest image
            logger.LogInformation("Pulling latest image '{Image}' for service '{ServiceName}'", currentImage, serviceName);

            var pullOutput = new System.Text.StringBuilder();
            var progress = new Progress<JSONMessage>(msg =>
            {
                if (!string.IsNullOrEmpty(msg.Status))
                {
                    pullOutput.AppendLine(msg.Status + (string.IsNullOrEmpty(msg.ProgressMessage) ? "" : $" {msg.ProgressMessage}"));
                }
            });

            await dockerClient.Images.CreateImageAsync(
                new ImagesCreateParameters
                {
                    FromImage = GetImageName(currentImage),
                    Tag = GetImageTag(currentImage)
                },
                null,
                progress,
                cancellationToken);

            result.PullOutput = pullOutput.ToString();
            logger.LogInformation("Image pull completed for service '{ServiceName}'", serviceName);

            // Step 2: Stop the container
            logger.LogInformation("Stopping container '{ContainerName}' for service '{ServiceName}'", containerName, serviceName);
            await dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters
            {
                WaitBeforeKillSeconds = 10
            }, cancellationToken);

            // Step 3: Remove the old container
            logger.LogInformation("Removing container '{ContainerName}' for service '{ServiceName}'", containerName, serviceName);
            await dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters
            {
                Force = false,
                RemoveVolumes = false
            }, cancellationToken);

            // Step 4: Create a new container with the same configuration but updated image
            logger.LogInformation("Creating new container for service '{ServiceName}'", serviceName);
            var createResponse = await dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = currentImage,
                Name = containerName,
                Hostname = inspection.Config.Hostname,
                User = inspection.Config.User,
                Env = inspection.Config.Env,
                Cmd = inspection.Config.Cmd,
                Entrypoint = inspection.Config.Entrypoint,
                WorkingDir = inspection.Config.WorkingDir,
                Labels = inspection.Config.Labels,
                ExposedPorts = inspection.Config.ExposedPorts,
                Volumes = inspection.Config.Volumes,
                StopSignal = inspection.Config.StopSignal,
                HostConfig = inspection.HostConfig,
                NetworkingConfig = BuildNetworkingConfig(inspection)
            }, cancellationToken);

            // Step 5: Start the new container
            logger.LogInformation("Starting new container '{ContainerName}' for service '{ServiceName}'", containerName, serviceName);
            await dockerClient.Containers.StartContainerAsync(createResponse.ID, new ContainerStartParameters(), cancellationToken);

            result.UpOutput = $"Container '{containerName}' recreated and started with latest image.";
            result.Success = true;
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;

            logger.LogInformation(
                "Docker update completed successfully for service '{ServiceName}' in {Duration:F2} seconds",
                serviceName,
                result.Duration.TotalSeconds);

            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Docker update cancelled for service '{ServiceName}'", serviceName);
            result.Success = false;
            result.ErrorOutput = "Operation was cancelled";
            throw;
        }
        catch (DockerApiException ex)
        {
            logger.LogError(ex, "Docker API error during update for service '{ServiceName}'", serviceName);
            result.Success = false;
            result.ErrorOutput = $"Docker API error: {ex.Message}";
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during Docker update for service '{ServiceName}'", serviceName);
            result.Success = false;
            result.ErrorOutput = $"Unexpected error: {ex.Message}";
            throw;
        }
        finally
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
        }
    }

    /// <summary>
    /// Finds a container belonging to a specific compose service by matching compose labels.
    /// </summary>
    private async Task<ContainerListResponse?> FindComposeContainerAsync(
        string composeFilePath, string serviceName, CancellationToken cancellationToken)
    {
        var containers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool>
                {
                    [$"com.docker.compose.service={serviceName}"] = true
                }
            }
        }, cancellationToken);

        // If multiple containers match the service name, prefer the one from the same compose file
        return containers
            .OrderByDescending(c =>
            {
                c.Labels.TryGetValue("com.docker.compose.project.config_files", out var configFiles);
                return configFiles?.Contains(composeFilePath, StringComparison.OrdinalIgnoreCase) == true ? 1 : 0;
            })
            .FirstOrDefault();
    }

    /// <summary>
    /// Builds the networking config from the inspected container to preserve network attachments.
    /// </summary>
    private static NetworkingConfig BuildNetworkingConfig(ContainerInspectResponse inspection)
    {
        var config = new NetworkingConfig
        {
            EndpointsConfig = new Dictionary<string, EndpointSettings>()
        };

        if (inspection.NetworkSettings?.Networks != null)
        {
            foreach (var (networkName, network) in inspection.NetworkSettings.Networks)
            {
                config.EndpointsConfig[networkName] = new EndpointSettings
                {
                    Aliases = network.Aliases,
                    IPAMConfig = network.IPAMConfig,
                    Links = network.Links,
                    NetworkID = network.NetworkID,
                    DriverOpts = network.DriverOpts
                };
            }
        }

        return config;
    }

    /// <summary>
    /// Extracts the image name (without tag) from a full image reference.
    /// </summary>
    private static string GetImageName(string image)
    {
        var lastColon = image.LastIndexOf(':');
        // Check it's not part of a registry port (e.g., registry:5000/image)
        if (lastColon > 0 && !image[lastColon..].Contains('/'))
        {
            return image[..lastColon];
        }
        return image;
    }

    /// <summary>
    /// Extracts the tag from a full image reference, defaulting to "latest".
    /// </summary>
    private static string GetImageTag(string image)
    {
        var lastColon = image.LastIndexOf(':');
        if (lastColon > 0 && !image[lastColon..].Contains('/'))
        {
            return image[(lastColon + 1)..];
        }
        return "latest";
    }
}

/// <summary>
/// Represents the result of a Docker Compose execution operation.
/// Encapsulates both success/failure status and detailed output for diagnostics.
/// </summary>
public class ComposeExecutionResult
{
    /// <summary>
    /// Indicates whether the update operation completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Output from the image pull operation.
    /// </summary>
    public string PullOutput { get; set; } = "";

    /// <summary>
    /// Output from the container recreate/start operation.
    /// </summary>
    public string UpOutput { get; set; } = "";

    /// <summary>
    /// Error output from failed operations.
    /// </summary>
    public string ErrorOutput { get; set; } = "";

    /// <summary>
    /// Total duration of the update operation.
    /// </summary>
    public TimeSpan Duration { get; set; }
}
