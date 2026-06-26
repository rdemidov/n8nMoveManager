namespace Application.Models;

public sealed record WorkflowSemanticDiffCollectionDto(
    string? Source,
    string? Target,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<WorkflowSemanticDiffDto> Workflows);

public sealed record WorkflowSemanticDiffDto(
    string? WorkflowId,
    string WorkflowName,
    string ChangeType,
    WorkflowSemanticDiffSummaryDto Summary,
    IReadOnlyList<NodeSemanticDiffDto> NodeChanges,
    IReadOnlyList<ConnectionSemanticDiffDto> ConnectionChanges,
    IReadOnlyList<CredentialSemanticDiffDto> CredentialChanges,
    IReadOnlyList<ParameterSemanticDiffDto> WorkflowSettingsChanges,
    string? OldFilePath,
    string? NewFilePath,
    IReadOnlyList<string> Warnings);

public sealed record WorkflowSemanticDiffSummaryDto(
    int AddedNodes,
    int RemovedNodes,
    int ModifiedNodes,
    int UnchangedNodes,
    int ChangedConnections,
    int ChangedCredentials,
    int ChangedWorkflowSettings);

public sealed record NodeSemanticDiffDto(
    string? NodeId,
    string NodeName,
    string NodeType,
    string ChangeType,
    IReadOnlyList<ParameterSemanticDiffDto> ParameterChanges,
    IReadOnlyList<CredentialSemanticDiffDto> CredentialChanges,
    IReadOnlyList<ParameterSemanticDiffDto> MetadataChanges);

public sealed record ParameterSemanticDiffDto(
    string Path,
    string? OldValuePreview,
    string? NewValuePreview,
    string ValueType,
    string Importance,
    string? OldValueFull = null,
    string? NewValueFull = null);

public sealed record CredentialSemanticDiffDto(
    string NodeName,
    string CredentialKey,
    string CredentialType,
    string? OldCredentialId,
    string? OldCredentialName,
    string? NewCredentialId,
    string? NewCredentialName);

public sealed record ConnectionSemanticDiffDto(
    string SourceNodeName,
    string TargetNodeName,
    int? OutputIndex,
    int? InputIndex,
    string ChangeType);

public sealed record PromotionSemanticSummaryDto(
    int AddedNodes,
    int RemovedNodes,
    int ModifiedNodes,
    int ChangedCredentials,
    int ChangedConnections,
    int ChangedWorkflowSettings);
