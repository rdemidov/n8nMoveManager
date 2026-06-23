namespace Application.Models;

public sealed record PromotionEnvironmentDto(
    Guid Id,
    string Name,
    string Key,
    string GitBranchName);

public sealed record PromotionPlanDto(
    PromotionEnvironmentDto SourceEnvironment,
    PromotionEnvironmentDto TargetEnvironment,
    DateTimeOffset GeneratedAt,
    string? SourceCommitSha,
    string? TargetCommitSha,
    string? BaseCommitSha,
    IReadOnlyList<PromotionWorkflowChangeDto> WorkflowChanges,
    int CredentialMappingsRequired,
    int CredentialMappingsFound,
    int ConflictCount,
    int UnresolvedConflictCount,
    IReadOnlyList<PromotionMissingMappingDto> MissingMappings,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> BlockingErrors,
    PromotionComparisonBaselineDto? Baseline);

public sealed record PromotionComparisonBaselineDto(
    string CommitSha,
    string? Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PromotionComparisonBaselineRequest(
    string SourceEnvironmentKey,
    string TargetEnvironmentKey,
    string? CommitSha,
    string? Label);

public sealed record PromotionWorkflowChangeDto(
    string WorkflowFilePath,
    string? WorkflowId,
    string WorkflowName,
    string ChangeType,
    string? Summary,
    bool IsSelectedByDefault,
    PromotionSemanticSummaryDto? SemanticSummary,
    WorkflowSemanticDiffDto? SemanticDiff,
    string? Resolution,
    IReadOnlyList<string> AvailableResolutions,
    bool IsConflict,
    string? ConflictReason,
    PromotionConflictDetailsDto? ConflictDetails);

public sealed record PromotionConflictDetailsDto(
    string WorkflowFilePath,
    string? WorkflowId,
    string WorkflowName,
    string SourceEnvironmentKey,
    string TargetEnvironmentKey,
    string? SourceCommitSha,
    string? TargetCommitSha,
    string? BaseCommitSha,
    WorkflowSemanticDiffDto? SourceVsBase,
    WorkflowSemanticDiffDto? TargetVsBase,
    WorkflowSemanticDiffDto? SourceVsTarget,
    string ConflictReason);

public sealed record PromotionMissingMappingDto(
    string WorkflowFilePath,
    string WorkflowName,
    string CredentialType,
    string? CredentialId,
    string? CredentialName);

public sealed record PromotionApplyRequest(
    string SourceEnvironmentKey,
    string TargetEnvironmentKey,
    IReadOnlyList<string>? SelectedWorkflowFiles,
    IReadOnlyList<PromotionWorkflowResolutionDto>? WorkflowResolutions,
    bool Confirmation,
    bool IncludeDeletions,
    bool ConfirmDeletions);

public sealed record PromotionApplyResult(
    bool CommitCreated,
    string? CommitSha,
    string? CommitMessage,
    int AppliedFilesCount,
    IReadOnlyList<string> AppliedFiles,
    IReadOnlyList<string> SkippedFiles,
    IReadOnlyList<string> DeletedFiles,
    IReadOnlyList<string> Warnings,
    string Message);

public sealed record PromotionWorkflowResolutionDto(
    string WorkflowFilePath,
    string Resolution);

public sealed record PromotionMergePreviewRequest(
    string SourceEnvironmentKey,
    string TargetEnvironmentKey,
    IReadOnlyList<PromotionWorkflowResolutionDto>? WorkflowResolutions,
    bool IncludeDeletions,
    bool ConfirmDeletions);

public sealed record PromotionMergePreviewDto(
    IReadOnlyList<string> WorkflowsToWrite,
    IReadOnlyList<string> WorkflowsToKeep,
    IReadOnlyList<string> WorkflowsToSkip,
    IReadOnlyList<string> WorkflowsToDelete,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> BlockingErrors,
    WorkflowSemanticDiffCollectionDto SemanticDiff,
    PromotionCredentialMappingSummaryDto CredentialMappingSummary,
    int AffectedWorkflowCount);

public sealed record PromotionCredentialMappingSummaryDto(
    int Required,
    int Found,
    IReadOnlyList<PromotionMissingMappingDto> MissingMappings);

public sealed record PromotionAuditCreate(
    Guid WorkspaceId,
    Guid SourceEnvironmentId,
    string SourceEnvironmentKey,
    Guid TargetEnvironmentId,
    string TargetEnvironmentKey,
    string Status,
    string? CommitSha,
    string? Summary,
    DateTimeOffset? AppliedAt);

public sealed record AppliedManualMergeAuditEntry(
    string WorkflowFilePath,
    string? CommitSha,
    DateTimeOffset AppliedAt);

public sealed record PromotionPreparedFile(
    string WorkflowFilePath,
    string Content);

public sealed record GitMergeBaseInfo(
    string? SourceCommitSha,
    string? TargetCommitSha,
    string? BaseCommitSha);
