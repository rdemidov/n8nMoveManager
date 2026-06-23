namespace Application.Models;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public sealed record EnvironmentN8nApiConfigDto(
    Guid EnvironmentId,
    string EnvironmentKey,
    bool Enabled,
    string BaseUrl,
    string DataTablesPath,
    string? DataTablesWritePathTemplate,
    string WorkflowApiPath,
    bool HasApiKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record EnvironmentN8nApiConfigRequest(
    bool Enabled,
    string? BaseUrl,
    string? DataTablesPath,
    string? DataTablesWritePathTemplate,
    string? WorkflowApiPath,
    string? ApiKey);

public sealed record WorkflowDeploymentPreviewRequest(string SourceEnvironmentKey, string TargetEnvironmentKey, IReadOnlyList<string> WorkflowFilePaths);
public sealed record WorkflowDeploymentItem(string WorkflowFilePath, string Name, string? TargetWorkflowId, string Action, IReadOnlyList<string> Warnings);
public sealed record WorkflowDeploymentPreview(string SourceEnvironmentKey, string TargetEnvironmentKey, IReadOnlyList<WorkflowDeploymentItem> Workflows, IReadOnlyList<string> BlockingErrors);
public sealed record WorkflowDeploymentApplyRequest(string SourceEnvironmentKey, string TargetEnvironmentKey, IReadOnlyList<string> WorkflowFilePaths, bool Confirmation, bool ActivateWorkflows);
public sealed record WorkflowDeploymentResult(string TargetEnvironmentKey, IReadOnlyList<string> CreatedWorkflowIds, IReadOnlyList<string> UpdatedWorkflowIds, IReadOnlyList<string> Warnings);
public sealed record WorkflowApiSyncResult(
    string EnvironmentKey,
    int FetchedWorkflowsCount,
    int ImportedWorkflowsCount,
    int ChangedFilesCount,
    string? CommitSha,
    bool SkippedCommit,
    int CredentialReferencesScanned,
    IReadOnlyList<string> Warnings);
public sealed record WorkflowApiReconciliationItem(string? WorkflowId, string Name, string? FilePath, string Status, bool CanSync);
public sealed record WorkflowApiReconciliationPreview(string EnvironmentKey, IReadOnlyList<WorkflowApiReconciliationItem> Items, int RemoteWorkflowCount, int LocalOnlyCount);
public sealed record WorkflowApiSyncSelectionRequest(IReadOnlyList<string> WorkflowIds);
public sealed record WorkflowHealthItem(string ExecutionId, string? WorkflowId, string? WorkflowName, string Status, DateTimeOffset? StartedAt, DateTimeOffset? StoppedAt);
public sealed record WorkflowHealthResult(string EnvironmentKey, IReadOnlyList<WorkflowHealthItem> RecentFailures);

public sealed record DataTableLiveDeployRequest(string SourceEnvironmentKey, string TargetEnvironmentKey, IReadOnlyList<string> TableIds, bool Confirmation);
public sealed record DataTableLiveDeployResult(string TargetEnvironmentKey, int DeployedCount, IReadOnlyList<string> DeployedTableIds);

public sealed record DataTableListItemDto(
    string Id,
    string Name,
    string ColumnsJson,
    int? RowCount,
    string EnvironmentKey,
    DateTimeOffset LastSyncedAt);

public sealed record DataTableSyncResult(
    string EnvironmentKey,
    int SyncedCount,
    int ChangedCount,
    string? CommitSha,
    bool SkippedCommit,
    IReadOnlyList<string> Warnings);

public sealed record DataTableComparisonItem(
    string Id,
    string Name,
    string Status,
    int? SourceRowCount,
    int? TargetRowCount);

public sealed record DataTableComparisonDto(
    string SourceEnvironmentKey,
    string TargetEnvironmentKey,
    IReadOnlyList<DataTableComparisonItem> Items);

public sealed record DataTablePromotionPlan(
    string SourceEnvironmentKey,
    string TargetEnvironmentKey,
    IReadOnlyList<DataTableComparisonItem> Changes,
    int ChangeCount,
    string SafetyNotice);

public sealed record DataTablePromotionApplyRequest(
    string SourceEnvironmentKey,
    string TargetEnvironmentKey,
    IReadOnlyList<string> TableIds,
    bool Confirmation);

public sealed record DataTablePromotionApplyResult(
    string SourceEnvironmentKey,
    string TargetEnvironmentKey,
    int StagedTablesCount,
    string? CommitSha,
    bool SkippedCommit);
