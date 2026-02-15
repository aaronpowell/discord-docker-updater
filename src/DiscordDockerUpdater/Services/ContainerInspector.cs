using Docker.DotNet;
using Docker.DotNet.Models;

namespace DiscordDockerUpdater.Services;

/// <summary>
/// Information about a container's Docker Compose origin, discovered via docker inspect labels.
/// </summary>
public record ContainerComposeInfo(string WorkingDir, string ConfigFile, string ServiceName, string ProjectName);

/// <summary>
/// Inspects running containers via the Docker socket to discover their Compose project details.
/// Uses Docker.DotNet SDK instead of shelling out to the Docker CLI.
/// </summary>
public class ContainerInspector(IDockerClient dockerClient, ILogger<ContainerInspector> logger)
{
    /// <summary>
    /// Inspects a container by name to extract its Docker Compose labels.
    /// Returns null if the container is not managed by Compose or doesn't exist.
    /// </summary>
    public async Task<ContainerComposeInfo?> InspectAsync(string containerName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            return null;

        logger.LogDebug("Inspecting container {ContainerName} for compose labels", containerName);

        try
        {
            var inspection = await dockerClient.Containers.InspectContainerAsync(containerName, cancellationToken);

            var labels = inspection.Config?.Labels;
            if (labels is null)
                return null;

            var hasWorkingDir = labels.TryGetValue("com.docker.compose.project.working_dir", out var workingDir);
            var hasConfigFiles = labels.TryGetValue("com.docker.compose.project.config_files", out var configFiles);
            var hasService = labels.TryGetValue("com.docker.compose.service", out var service);
            var hasProject = labels.TryGetValue("com.docker.compose.project", out var project);

            if (!hasService || string.IsNullOrEmpty(service) || !hasWorkingDir || string.IsNullOrEmpty(workingDir))
            {
                logger.LogInformation("Container {ContainerName} is not managed by Docker Compose", containerName);
                return null;
            }

            // Config files may contain multiple comma-separated paths; take the first
            var configFile = configFiles?.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                ?? Path.Combine(workingDir, "docker-compose.yml");

            var info = new ContainerComposeInfo(workingDir, configFile, service, project ?? "");
            logger.LogInformation(
                "Container {ContainerName} belongs to compose project {Project}, service {Service}, config {ConfigFile}",
                containerName, info.ProjectName, info.ServiceName, info.ConfigFile);

            return info;
        }
        catch (DockerContainerNotFoundException)
        {
            logger.LogWarning("Container {ContainerName} not found", containerName);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to inspect container {ContainerName}", containerName);
            return null;
        }
    }
}
