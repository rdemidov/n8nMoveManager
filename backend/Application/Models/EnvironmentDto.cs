namespace Application.Models;

public sealed record EnvironmentDto(
    Guid Id,
    string Name,
    string Key,
    string? Description,
    string GitBranch,
    string GitBranchName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsDefault);

public sealed record EnvironmentRequest(
    string Name,
    string? Key,
    string? Description,
    string? GitBranchName);

public sealed record EnvironmentDeleteResult(string Message);

public sealed record EnvironmentClearResult(
    string Message,
    string EnvironmentKey,
    int RemovedWorkflowFilesCount,
    int RemovedWorkflowMetadataCount,
    int RemovedCredentialReferencesCount,
    int RemovedEnvironmentCredentialsCount,
    int RemovedLogicalCredentialMappingsCount,
    int ChangedFilesCount,
    string? CommitSha,
    bool SkippedCommit);

public sealed record EnvironmentCompareDto(
    string Source,
    string Target,
    IReadOnlyList<GitDiffFileDto> Files);
