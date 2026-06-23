namespace Application.Models;

public sealed record DockerCommandResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    string Command,
    bool TimedOut = false)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

public sealed record DockerStatusDto(
    bool Available,
    string Message,
    string? Version,
    IReadOnlyList<string> Logs,
    TimeSpan Duration);

public sealed record EnvironmentDockerConfigDto(
    Guid EnvironmentId,
    string EnvironmentKey,
    bool DockerEnabled,
    string ContainerName,
    string N8nCliCommand,
    string TempContainerPath,
    string? TempHostImportPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record EnvironmentDockerConfigRequest(
    bool DockerEnabled,
    string? ContainerName,
    string? N8nCliCommand,
    string? TempContainerPath,
    string? TempHostImportPath);

public sealed record DockerExportResultDto(
    string Status,
    string EnvironmentKey,
    string ContainerName,
    int ExportedWorkflowsCount,
    int ImportedWorkflowsCount,
    int ChangedFilesCount,
    string? CommitSha,
    bool SkippedCommit,
    int CredentialReferencesScanned,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Logs,
    TimeSpan Duration);
