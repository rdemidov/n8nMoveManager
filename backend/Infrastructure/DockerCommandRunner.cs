using System.Diagnostics;
using Application.Contracts;
using Application.Models;

namespace Infrastructure;

public sealed class DockerCommandRunner : IDockerCommandRunner
{
    public Task<DockerCommandResult> CheckDockerAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        RunDockerAsync(["--version"], timeout, cancellationToken);

    public Task<DockerCommandResult> CheckContainerExistsAsync(string containerName, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunDockerAsync(["inspect", "-f", "{{.State.Running}}", containerName], timeout, cancellationToken);

    public Task<DockerCommandResult> DockerExecAsync(string containerName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunDockerAsync(["exec", containerName, .. arguments], timeout, cancellationToken);

    public Task<DockerCommandResult> DockerCpAsync(string containerSourcePath, string hostDestinationPath, TimeSpan timeout, CancellationToken cancellationToken) =>
        RunDockerAsync(["cp", containerSourcePath, hostDestinationPath], timeout, cancellationToken);

    private static async Task<DockerCommandResult> RunDockerAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var command = $"docker {string.Join(' ', arguments)}";
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new DockerCommandResult(process.ExitCode, stdout, stderr, stopwatch.Elapsed, command);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new DockerCommandResult(124, string.Empty, $"Command timed out after {timeout.TotalSeconds:N0} seconds.", stopwatch.Elapsed, command, TimedOut: true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new DockerCommandResult(127, string.Empty, ex.Message, stopwatch.Elapsed, command);
        }
    }
}
