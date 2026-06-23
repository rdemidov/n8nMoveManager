using System.Diagnostics;
using System.Text.Json;
using Application.Contracts;
using Application.Models;

namespace Application;

public sealed class DockerN8nExportService
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromSeconds(30);

    private readonly IEnvironmentDockerConfigStore _configStore;
    private readonly IDockerCommandRunner _docker;
    private readonly IWorkflowImportService _importService;

    public DockerN8nExportService(
        IEnvironmentDockerConfigStore configStore,
        IDockerCommandRunner docker,
        IWorkflowImportService importService)
    {
        _configStore = configStore;
        _docker = docker;
        _importService = importService;
    }

    public async Task<DockerStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var result = await _docker.CheckDockerAvailableAsync(ShortTimeout, cancellationToken);
        var logs = BuildLogs("docker --version", result);
        var version = result.Succeeded ? FirstNonEmptyLine(result.Stdout) : null;
        var message = result.Succeeded
            ? "Docker CLI is available."
            : DockerFailureMessage("Docker CLI is not available. Install Docker or make the Docker socket available to this app.", result);
        return new DockerStatusDto(result.Succeeded, message, version, logs, result.Duration);
    }

    public async Task<DockerExportResultDto> TestAsync(string environmentKey, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var logs = new List<string>();
        var warnings = new List<string>();
        var config = await GetConfigAsync(environmentKey, requireEnabled: false, cancellationToken);

        var docker = await _docker.CheckDockerAvailableAsync(ShortTimeout, cancellationToken);
        logs.AddRange(BuildLogs("docker --version", docker));
        if (!docker.Succeeded)
        {
            throw new WorkflowImportException(DockerFailureMessage("Docker CLI is not available or the Docker socket is not mounted.", docker));
        }

        ValidateContainerName(config.ContainerName);
        var container = await _docker.CheckContainerExistsAsync(config.ContainerName, ShortTimeout, cancellationToken);
        logs.AddRange(BuildLogs($"docker inspect {config.ContainerName}", container));
        if (!container.Succeeded)
        {
            throw new WorkflowImportException(DockerFailureMessage($"Container '{config.ContainerName}' was not found or is not running.", container));
        }

        var cli = await _docker.DockerExecAsync(config.ContainerName, [config.N8nCliCommand, "--version"], ShortTimeout, cancellationToken);
        logs.AddRange(BuildLogs($"docker exec {config.ContainerName} {config.N8nCliCommand} --version", cli));
        if (!cli.Succeeded)
        {
            throw new WorkflowImportException(DockerFailureMessage($"n8n CLI command '{config.N8nCliCommand}' could not be run inside container '{config.ContainerName}'.", cli));
        }

        logs.Add($"Prepared export command: docker exec {config.ContainerName} {config.N8nCliCommand} export:workflow --all --output={config.TempContainerPath}");
        return new DockerExportResultDto("ready", environmentKey, config.ContainerName, 0, 0, 0, null, true, 0, warnings, logs, stopwatch.Elapsed);
    }

    public async Task<DockerExportResultDto> ExportWorkflowsAsync(string environmentKey, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var logs = new List<string>();
        var warnings = new List<string>();
        var config = await GetConfigAsync(environmentKey, requireEnabled: true, cancellationToken);

        var docker = await _docker.CheckDockerAvailableAsync(ShortTimeout, cancellationToken);
        logs.AddRange(BuildLogs("docker --version", docker));
        if (!docker.Succeeded)
        {
            throw new WorkflowImportException(DockerFailureMessage("Docker CLI is not available or the Docker socket is not mounted.", docker));
        }

        ValidateContainerName(config.ContainerName);
        var container = await _docker.CheckContainerExistsAsync(config.ContainerName, ShortTimeout, cancellationToken);
        logs.AddRange(BuildLogs($"docker inspect {config.ContainerName}", container));
        if (!container.Succeeded)
        {
            throw new WorkflowImportException(DockerFailureMessage($"Container '{config.ContainerName}' was not found or is not running.", container));
        }

        var exportCommand = new[] { config.N8nCliCommand, "export:workflow", "--all", $"--output={config.TempContainerPath}" };
        var export = await _docker.DockerExecAsync(config.ContainerName, exportCommand, ExportTimeout, cancellationToken);
        logs.AddRange(BuildLogs($"docker exec {config.ContainerName} {string.Join(' ', exportCommand)}", export));
        if (!export.Succeeded)
        {
            throw new WorkflowImportException(DockerFailureMessage("n8n workflow export command failed.", export));
        }

        var localTempPath = BuildHostTempPath(config.TempHostImportPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localTempPath)!);
            var copy = await _docker.DockerCpAsync($"{config.ContainerName}:{config.TempContainerPath}", localTempPath, CopyTimeout, cancellationToken);
            logs.AddRange(BuildLogs($"docker cp {config.ContainerName}:{config.TempContainerPath} {localTempPath}", copy));
            if (!copy.Succeeded)
            {
                throw new WorkflowImportException(DockerFailureMessage("Docker copy failed while retrieving the exported workflow file.", copy));
            }

            if (!File.Exists(localTempPath))
            {
                throw new WorkflowImportException($"Docker export file was not copied to '{localTempPath}'.");
            }

            var content = await File.ReadAllTextAsync(localTempPath, cancellationToken);
            var exportedCount = CountExportedWorkflows(content);
            if (exportedCount == 0)
            {
                throw new WorkflowImportException("n8n export completed, but the exported JSON did not contain any workflows.");
            }

            var import = await _importService.ImportAsync(
                environmentKey,
                [new WorkflowUploadSource("n8n-docker-export.json", content)],
                $"Import workflows from Docker container {config.ContainerName}: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}",
                cancellationToken);

            logs.Add(import.Message);
            if (import.CommitSha is null)
            {
                warnings.Add("No changes detected. No commit was created.");
            }

            return new DockerExportResultDto(
                import.CommitSha is null ? "no-changes" : "imported",
                environmentKey,
                config.ContainerName,
                exportedCount,
                import.ImportedWorkflowsCount,
                import.ChangedFilesCount,
                import.CommitSha,
                import.CommitSha is null,
                import.CredentialReferencesScanned,
                warnings,
                logs,
                stopwatch.Elapsed);
        }
        finally
        {
            if (File.Exists(localTempPath))
            {
                File.Delete(localTempPath);
            }
        }
    }

    private async Task<EnvironmentDockerConfigDto> GetConfigAsync(string environmentKey, bool requireEnabled, CancellationToken cancellationToken)
    {
        var config = await _configStore.GetAsync(environmentKey, cancellationToken);
        if (requireEnabled && !config.DockerEnabled)
        {
            throw new WorkflowImportException($"Docker integration is not enabled for environment '{environmentKey}'.");
        }

        if (string.IsNullOrWhiteSpace(config.ContainerName))
        {
            throw new WorkflowImportException("Docker container name is required.");
        }

        if (string.IsNullOrWhiteSpace(config.N8nCliCommand))
        {
            throw new WorkflowImportException("n8n CLI command is required.");
        }

        if (string.IsNullOrWhiteSpace(config.TempContainerPath))
        {
            throw new WorkflowImportException("Temporary container export path is required.");
        }

        return config;
    }

    private static int CountExportedWorkflows(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Object => 1,
                JsonValueKind.Array => document.RootElement.GetArrayLength(),
                _ => throw new WorkflowImportException("Exported JSON must be a workflow object or an array of workflow objects.")
            };
        }
        catch (JsonException ex)
        {
            throw new WorkflowImportException($"Invalid exported JSON: {ex.Message}");
        }
    }

    private static string BuildHostTempPath(string? configuredPath) =>
        string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Path.GetTempPath(), "n8n-move-manager", $"workflows-{Guid.NewGuid():N}.json")
            : Path.GetFullPath(configuredPath);

    private static void ValidateContainerName(string containerName)
    {
        if (containerName.Any(char.IsWhiteSpace))
        {
            throw new WorkflowImportException("Docker container name cannot contain whitespace.");
        }
    }

    private static IReadOnlyList<string> BuildLogs(string label, DockerCommandResult result)
    {
        var logs = new List<string> { $"{label} exited with code {result.ExitCode} in {result.Duration.TotalMilliseconds:N0} ms." };
        if (result.TimedOut)
        {
            logs.Add("Command timed out.");
        }
        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            logs.Add($"stdout: {result.Stdout.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            logs.Add($"stderr: {result.Stderr.Trim()}");
        }
        return logs;
    }

    private static string DockerFailureMessage(string prefix, DockerCommandResult result)
    {
        var detail = !string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stderr.Trim()
            : !string.IsNullOrWhiteSpace(result.Stdout)
                ? result.Stdout.Trim()
                : result.TimedOut
                    ? "The command timed out."
                    : $"Exit code {result.ExitCode}.";
        return $"{prefix} {detail}";
    }

    private static string? FirstNonEmptyLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
}
