namespace Application.Models;

public sealed record ManualMergeCreateRequest(
    string SourceEnvironmentKey,
    string TargetEnvironmentKey,
    string WorkflowFilePath,
    string? SourceCommitSha,
    string? TargetCommitSha);

public sealed record ManualMergeSessionDto(
    string Id,
    string SourceEnvironmentKey,
    string TargetEnvironmentKey,
    string WorkflowFilePath,
    string? SourceCommitSha,
    string? TargetCommitSha,
    string? BaseCommitSha,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ManualMergeWorkflowSummaryDto SourceWorkflow,
    ManualMergeWorkflowSummaryDto TargetWorkflow,
    WorkflowSemanticDiffDto SemanticDiff,
    ManualMergeSelectionDto Selection);

public sealed record ManualMergeWorkflowSummaryDto(
    string? WorkflowId,
    string WorkflowName,
    bool Active,
    int NodesCount,
    IReadOnlyList<ManualMergeCredentialReferenceDto> CredentialReferences);

public sealed record ManualMergeCredentialReferenceDto(
    string NodeName,
    string CredentialKey,
    string CredentialType,
    string? CredentialId,
    string? CredentialName,
    bool IsMapped,
    string? MappedCredentialId,
    string? MappedCredentialName);

public sealed record ManualMergeSelectionDto(
    IReadOnlyList<WorkflowSettingMergeSelectionDto> WorkflowSettingsSelections,
    IReadOnlyList<NodeMergeSelectionDto> NodeSelections,
    IReadOnlyList<ParameterMergeSelectionDto> ParameterSelections,
    string ConnectionSelection,
    string CredentialMappingMode);

public sealed record WorkflowSettingMergeSelectionDto(
    string PropertyName,
    string? SourceValuePreview,
    string? TargetValuePreview,
    string SelectedSide);

public sealed record NodeMergeSelectionDto(
    string NodeMatchKey,
    string? SourceNodeId,
    string? TargetNodeId,
    string NodeName,
    string NodeType,
    string ChangeType,
    string Resolution);

public sealed record ParameterMergeSelectionDto(
    string NodeMatchKey,
    string ParameterPath,
    string? SourceValuePreview,
    string? TargetValuePreview,
    string SelectedSide);

public sealed record ManualMergeSelectionUpdateRequest(
    ManualMergeSelectionDto Selection);

public sealed record ManualMergeResultDto(
    string ResultWorkflowJson,
    string ValidationStatus,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> BlockingErrors,
    IReadOnlyList<string> InfoMessages,
    WorkflowSemanticDiffDto SemanticDiffResultVsTarget,
    WorkflowSemanticDiffDto SemanticDiffResultVsSource);

public sealed record ManualMergeApplyRequest(
    bool Confirmation);

public sealed record ManualMergeApplyResult(
    bool CommitCreated,
    string? CommitSha,
    string? CommitMessage,
    string WorkflowFilePath,
    IReadOnlyList<string> Warnings,
    string Message);

public sealed record ManualMergeValidationResult(
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> BlockingErrors,
    IReadOnlyList<string> InfoMessages)
{
    public string Status => BlockingErrors.Count == 0 ? "valid" : "blocked";
}
