namespace Application.Models;

public sealed record ScheduledJobDto(
    Guid Id,
    string Name,
    string JobType,
    Guid EnvironmentId,
    string EnvironmentKey,
    string EnvironmentName,
    string CronExpression,
    string Timezone,
    bool IsEnabled,
    string ConfigJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt,
    string? LastRunStatus);

public sealed record ScheduledJobRequest(
    string Name,
    string JobType,
    Guid EnvironmentId,
    string CronExpression,
    string Timezone,
    bool IsEnabled,
    string? ConfigJson);

public sealed record ScheduledJobRunDto(
    Guid Id,
    Guid ScheduledJobId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Status,
    string[] Logs,
    string? ErrorMessage,
    string? CommitSha,
    string? ResultJson);

public sealed record ScheduledJobRunSummaryDto(
    Guid Id,
    Guid ScheduledJobId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Status,
    string? ErrorMessage,
    string? CommitSha);

public sealed record ScheduledJobRunNowResult(Guid RunId, string Message);

public sealed record DockerN8nWorkflowExportJobConfig(
    string? ContainerName,
    bool ExportWorkflows = true,
    bool ExportCredentials = false,
    bool CommitChanges = true,
    bool ScanCredentials = true,
    bool DeleteTempFiles = true);

public sealed record WorkspaceBackupJobConfig(
    bool IncludeGitRepo = true,
    bool IncludeDatabase = true,
    int RetentionCount = 10);

public sealed record N8nApiWorkflowSyncJobConfig(bool CommitChanges = true);
