using System.Text.RegularExpressions;
using DiscordDockerUpdater.Configuration;
using Microsoft.Extensions.Options;

namespace DiscordDockerUpdater.Services;

/// <summary>
/// Updates Docker Compose files to pin the image reference for a service to a specific digest.
/// This keeps the compose source file in sync with what is actually running.
/// </summary>
public partial class ComposeFileUpdater(
    IOptions<BotConfiguration> config,
    ILogger<ComposeFileUpdater> logger)
{
    /// <summary>
    /// Updates the image reference for a service in a compose file to pin to the given digest.
    /// Only runs when the UpdateSource configuration flag is enabled.
    /// </summary>
    /// <param name="composeFilePath">Path to the docker-compose.yml file</param>
    /// <param name="serviceName">The service name within the compose file</param>
    /// <param name="newImageWithDigest">The full image reference with digest (e.g. repo/name@sha256:abc...)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the file was updated, false otherwise</returns>
    public async Task<bool> UpdateImageReferenceAsync(
        string composeFilePath,
        string serviceName,
        string newImageWithDigest,
        CancellationToken cancellationToken = default)
    {
        if (!config.Value.UpdateSource)
        {
            logger.LogDebug("UpdateSource is disabled, skipping compose file update for service '{ServiceName}'", serviceName);
            return false;
        }

        if (string.IsNullOrWhiteSpace(composeFilePath) ||
            string.IsNullOrWhiteSpace(serviceName) ||
            string.IsNullOrWhiteSpace(newImageWithDigest))
        {
            logger.LogWarning(
                "Cannot update compose file: missing required parameters (path={Path}, service={Service}, image={Image})",
                composeFilePath, serviceName, newImageWithDigest);
            return false;
        }

        if (!File.Exists(composeFilePath))
        {
            logger.LogWarning("Compose file not found: {Path}", composeFilePath);
            return false;
        }

        try
        {
            var content = await File.ReadAllTextAsync(composeFilePath, cancellationToken);
            var updatedContent = UpdateImageInYaml(content, serviceName, newImageWithDigest);

            if (updatedContent == content)
            {
                logger.LogInformation(
                    "Compose file already up to date for service '{ServiceName}' in {Path}",
                    serviceName, composeFilePath);
                return false;
            }

            await File.WriteAllTextAsync(composeFilePath, updatedContent, cancellationToken);

            logger.LogInformation(
                "Updated compose file {Path} for service '{ServiceName}' to image '{Image}'",
                composeFilePath, serviceName, newImageWithDigest);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to update compose file {Path} for service '{ServiceName}'",
                composeFilePath, serviceName);
            return false;
        }
    }

    /// <summary>
    /// Finds the service block in the YAML content and updates its image line.
    /// Uses a simple but robust approach: finds the service header, then the next
    /// image: line within that service's indentation level.
    /// </summary>
    internal static string UpdateImageInYaml(string yaml, string serviceName, string newImageReference)
    {
        // Detect line ending style to preserve it
        var lineEnding = yaml.Contains("\r\n") ? "\r\n" : "\n";
        var lines = yaml.Split(lineEnding);
        var result = new List<string>(lines.Length);

        var inTargetService = false;
        var serviceIndent = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            var currentIndent = line.Length - line.TrimStart().Length;

            // Look for the service name as a YAML key (e.g., "  servicename:")
            if (trimmed == $"{serviceName}:" || trimmed.StartsWith($"{serviceName}: "))
            {
                // Verify this is under a services: block by checking if we can find one above
                if (IsUnderServicesBlock(lines, i, currentIndent))
                {
                    inTargetService = true;
                    serviceIndent = currentIndent;
                    result.Add(line);
                    continue;
                }
            }

            // If we're in the target service block
            if (inTargetService)
            {
                // Check if we've left the service block (same or lower indent, non-empty line)
                if (trimmed.Length > 0 && currentIndent <= serviceIndent)
                {
                    inTargetService = false;
                }
                else if (ImageLineRegex().IsMatch(trimmed))
                {
                    // Found the image: line — replace the value, preserving indentation and quoting
                    var prefix = line[..currentIndent];
                    var quote = DetectQuoteStyle(trimmed);
                    result.Add($"{prefix}image: {quote}{newImageReference}{quote}");
                    inTargetService = false; // Done with this service
                    continue;
                }
            }

            result.Add(line);
        }

        return string.Join(lineEnding, result);
    }

    /// <summary>
    /// Checks whether the line at the given index is under a "services:" block.
    /// </summary>
    private static bool IsUnderServicesBlock(string[] lines, int serviceLineIndex, int serviceIndent)
    {
        // Walk backwards to find a parent key at lower indentation
        for (var i = serviceLineIndex - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            var indent = lines[i].Length - lines[i].TrimStart().Length;

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            if (indent < serviceIndent)
            {
                return trimmed.StartsWith("services:");
            }
        }

        return false;
    }

    /// <summary>
    /// Detects whether the image value uses single quotes, double quotes, or no quotes.
    /// </summary>
    private static string DetectQuoteStyle(string trimmedLine)
    {
        var afterColon = trimmedLine["image:".Length..].TrimStart();
        if (afterColon.StartsWith('"'))
            return "\"";
        if (afterColon.StartsWith('\''))
            return "'";
        return "";
    }

    /// <summary>
    /// Builds the full image reference with digest from the image name and digest.
    /// </summary>
    public static string BuildImageWithDigest(string image, string digest)
    {
        // Strip any existing tag or digest from the image name
        var imageName = StripTagAndDigest(image);
        return $"{imageName}@{digest}";
    }

    /// <summary>
    /// Strips the tag and/or digest from an image reference, returning just the name.
    /// Handles: repo/name:tag, repo/name@sha256:..., repo/name:tag@sha256:...
    /// </summary>
    private static string StripTagAndDigest(string image)
    {
        // Remove digest first if present
        var atIndex = image.IndexOf('@');
        if (atIndex > 0)
        {
            image = image[..atIndex];
        }

        // Remove tag, being careful about registry port (e.g., registry:5000/image)
        var lastColon = image.LastIndexOf(':');
        if (lastColon > 0 && !image[lastColon..].Contains('/'))
        {
            image = image[..lastColon];
        }

        return image;
    }

    [GeneratedRegex(@"^image:\s")]
    private static partial Regex ImageLineRegex();
}
