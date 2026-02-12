using System.Diagnostics;
using System.Text;

namespace DiscordDockerUpdater.Services;

/// <summary>
/// Executes Docker Compose commands (pull and up) for container updates.
/// Implements robust process execution with timeout handling and comprehensive logging.
/// </summary>
public class DockerComposeExecutor(ILogger<DockerComposeExecutor> logger)
{

    /// <summary>
    /// Pulls the latest image and recreates the container for a given service.
    /// Follows the Docker Compose best practice of pull-then-up for zero-downtime updates.
    /// </summary>
    /// <param name="composeFilePath">Absolute path to the docker-compose.yml</param>
    /// <param name="serviceName">The service name within the compose file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result with success/failure, stdout, stderr</returns>
    public async Task<ComposeExecutionResult> UpdateServiceAsync(
        string composeFilePath, 
        string serviceName, 
        CancellationToken cancellationToken = default)
    {
        // Input validation following fail-fast principle
        if (string.IsNullOrWhiteSpace(composeFilePath))
        {
            throw new ArgumentException("Compose file path cannot be null or empty", nameof(composeFilePath));
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));
        }

        if (!File.Exists(composeFilePath))
        {
            throw new FileNotFoundException($"Compose file not found: {composeFilePath}", composeFilePath);
        }

        var workingDirectory = Path.GetDirectoryName(composeFilePath) 
            ?? throw new InvalidOperationException($"Could not determine directory for compose file: {composeFilePath}");

        logger.LogInformation(
            "Starting Docker Compose update for service '{ServiceName}' using compose file '{ComposePath}'",
            serviceName, 
            composeFilePath);

        var stopwatch = Stopwatch.StartNew();
        var result = new ComposeExecutionResult();

        try
        {
            // Step 1: Pull the latest image
            logger.LogInformation("Executing docker compose pull for service '{ServiceName}'", serviceName);
            
            var pullResult = await RunProcessAsync(
                command: "docker",
                arguments: $"compose -f \"{composeFilePath}\" pull {serviceName}",
                workingDirectory: workingDirectory,
                timeout: TimeSpan.FromMinutes(5),
                cancellationToken: cancellationToken);

            result.PullOutput = pullResult.StdOut;

            if (pullResult.ExitCode != 0)
            {
                result.Success = false;
                result.ErrorOutput = pullResult.StdErr;
                logger.LogError(
                    "Docker compose pull failed for service '{ServiceName}' with exit code {ExitCode}. Error: {Error}",
                    serviceName,
                    pullResult.ExitCode,
                    pullResult.StdErr);
                return result;
            }

            logger.LogInformation("Docker compose pull completed successfully for service '{ServiceName}'", serviceName);

            // Step 2: Recreate and start the container with the new image
            logger.LogInformation("Executing docker compose up for service '{ServiceName}'", serviceName);

            var upResult = await RunProcessAsync(
                command: "docker",
                arguments: $"compose -f \"{composeFilePath}\" up -d {serviceName}",
                workingDirectory: workingDirectory,
                timeout: TimeSpan.FromMinutes(2),
                cancellationToken: cancellationToken);

            result.UpOutput = upResult.StdOut;

            if (upResult.ExitCode != 0)
            {
                result.Success = false;
                result.ErrorOutput = upResult.StdErr;
                logger.LogError(
                    "Docker compose up failed for service '{ServiceName}' with exit code {ExitCode}. Error: {Error}",
                    serviceName,
                    upResult.ExitCode,
                    upResult.StdErr);
                return result;
            }

            // Success!
            result.Success = true;
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;

            logger.LogInformation(
                "Docker Compose update completed successfully for service '{ServiceName}' in {Duration:F2} seconds",
                serviceName,
                result.Duration.TotalSeconds);

            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Docker Compose update cancelled for service '{ServiceName}'", serviceName);
            result.Success = false;
            result.ErrorOutput = "Operation was cancelled";
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during Docker Compose update for service '{ServiceName}'", serviceName);
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
    /// Executes a process with timeout and cancellation support.
    /// Implements async/await pattern for responsive execution.
    /// </summary>
    private async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string command,
        string arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        logger.LogDebug(
            "Running process: {Command} {Arguments} in directory {WorkingDirectory}",
            command,
            arguments,
            workingDirectory);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = processStartInfo };
        
        // StringBuilders for thread-safe output capture
        var stdOutBuilder = new StringBuilder();
        var stdErrBuilder = new StringBuilder();

        // Event handlers for async output reading
        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                stdOutBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                stdErrBuilder.AppendLine(e.Data);
            }
        };

        // Start the process
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {command}");
        }

        // Begin async reading of output streams
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Create a combined cancellation token for timeout + external cancellation
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, 
            timeoutCts.Token);

        try
        {
            // Wait for process to exit with cancellation support
            await process.WaitForExitAsync(combinedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancellation occurred - kill the process
            logger.LogWarning(
                "Process {Command} timed out or was cancelled. Killing process.",
                command);

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to kill timed-out process {Command}", command);
            }

            throw;
        }

        // Wait for async readers to complete
        await Task.Delay(100, CancellationToken.None); // Small delay to ensure output is fully captured

        var exitCode = process.ExitCode;
        var stdOut = stdOutBuilder.ToString();
        var stdErr = stdErrBuilder.ToString();

        logger.LogDebug(
            "Process {Command} exited with code {ExitCode}",
            command,
            exitCode);

        return (exitCode, stdOut, stdErr);
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
    /// Standard output from the docker compose pull command.
    /// </summary>
    public string PullOutput { get; set; } = "";

    /// <summary>
    /// Standard output from the docker compose up command.
    /// </summary>
    public string UpOutput { get; set; } = "";

    /// <summary>
    /// Combined error output from both commands.
    /// </summary>
    public string ErrorOutput { get; set; } = "";

    /// <summary>
    /// Total duration of the update operation.
    /// </summary>
    public TimeSpan Duration { get; set; }
}
