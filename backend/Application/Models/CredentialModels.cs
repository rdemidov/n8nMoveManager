namespace Application.Models;

public sealed record CredentialReferenceDto(
    Guid Id,
    string EnvironmentKey,
    string? WorkflowId,
    string WorkflowName,
    string WorkflowFilePath,
    string? NodeId,
    string NodeName,
    string NodeType,
    string CredentialType,
    string? CredentialId,
    string? CredentialName,
    DateTimeOffset DetectedAt);

public sealed record EnvironmentCredentialDto(
    Guid Id,
    string EnvironmentKey,
    string CredentialType,
    string? CredentialId,
    string? CredentialName,
    int ReferenceCount,
    DateTimeOffset LastDetectedAt);

public sealed record LogicalCredentialDto(
    Guid Id,
    string Key,
    string DisplayName,
    IReadOnlyList<LogicalCredentialMappingDto> Mappings);

public sealed record LogicalCredentialMappingDto(
    Guid Id,
    Guid EnvironmentId,
    string EnvironmentKey,
    string EnvironmentName,
    Guid EnvironmentCredentialId,
    string CredentialType,
    string? CredentialId,
    string? CredentialName,
    int ReferenceCount,
    DateTimeOffset LastDetectedAt);

public sealed record LogicalCredentialRequest(string Key, string DisplayName);

public sealed record LogicalCredentialMappingRequest(
    Guid LogicalCredentialId,
    string EnvironmentKey,
    Guid EnvironmentCredentialId);

public sealed record LogicalCredentialPairMappingRequest(
    Guid LogicalCredentialId,
    string SourceEnvironmentKey,
    Guid SourceEnvironmentCredentialId,
    string TargetEnvironmentKey,
    Guid TargetEnvironmentCredentialId);

public sealed record CredentialScanItem(
    string? WorkflowExternalId,
    string WorkflowName,
    string WorkflowFilePath,
    string? NodeId,
    string NodeName,
    string NodeType,
    string CredentialType,
    string? CredentialId,
    string? CredentialName);

public sealed record ExportValidationIssue(
    string Severity,
    string Message,
    string? WorkflowName = null,
    string? WorkflowFilePath = null,
    string? NodeName = null,
    string? CredentialType = null,
    string? CredentialId = null,
    string? CredentialName = null);

public sealed record ExportValidationResult(
    bool CanExport,
    IReadOnlyList<ExportValidationIssue> Issues);

public sealed record RemapPreviewItem(
    string WorkflowName,
    string WorkflowFilePath,
    string NodeName,
    string CredentialType,
    string? SourceCredentialId,
    string? SourceCredentialName,
    string? TargetCredentialId,
    string? TargetCredentialName,
    string? LogicalKey,
    string Status);

public sealed record RemapPreviewResult(IReadOnlyList<RemapPreviewItem> Items);

public sealed record AiCredentialMappingRequest(
    string SourceEnvironmentKey,
    string TargetEnvironmentKey);

public sealed record AiCredentialMappingResult(
    string SourceEnvironmentKey,
    string TargetEnvironmentKey,
    int SuggestedMappingsCount,
    int AppliedMappingsCount,
    int CreatedLogicalCredentialsCount,
    IReadOnlyList<AiCredentialMappingAppliedItem> Items,
    IReadOnlyList<string> Warnings);

public sealed record AiCredentialMappingAppliedItem(
    string LogicalKey,
    string DisplayName,
    Guid? LogicalCredentialId,
    Guid SourceEnvironmentCredentialId,
    Guid TargetEnvironmentCredentialId,
    string SourceCredentialLabel,
    string TargetCredentialLabel,
    string Reason,
    string Confidence,
    bool Applied,
    string? SkippedReason);
