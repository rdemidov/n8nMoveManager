namespace Application.Models;

public sealed record CommitFileItemDto(
    string FilePath,
    string FileName,
    long SizeBytes);

public sealed record CommitFileContentDto(
    string CommitSha,
    string FilePath,
    string Content);

public sealed record RestoreWorkflowRequest(
    string? CommitSha,
    string? FilePath,
    bool Confirmation);

public sealed record RestoreEnvironmentRequest(
    string? CommitSha,
    bool Confirmation,
    bool IncludeDeletedFiles);

public sealed record RestorePreviewDto(
    GitCommitDto SelectedCommit,
    GitCommitDto? CurrentCommit,
    IReadOnlyList<string> FilesToRestore,
    IReadOnlyList<string> FilesToAdd,
    IReadOnlyList<string> FilesToModify,
    IReadOnlyList<string> FilesThatWouldBeDeleted,
    IReadOnlyList<string> Warnings,
    WorkflowSemanticDiffCollectionDto SemanticDiffSummary);

public sealed record RestoreWorkflowResult(
    string EnvironmentKey,
    string SourceCommitSha,
    string FilePath,
    bool CommitCreated,
    string? NewCommitSha,
    string? CommitMessage,
    IReadOnlyList<string> Warnings,
    WorkflowSemanticDiffDto SemanticDiff);

public sealed record RestoreEnvironmentResult(
    string EnvironmentKey,
    string SourceCommitSha,
    bool CommitCreated,
    string? NewCommitSha,
    string? CommitMessage,
    int RestoredFilesCount,
    int AddedFilesCount,
    int ModifiedFilesCount,
    int DeletedFilesCount,
    IReadOnlyList<string> Warnings,
    RestorePreviewDto Preview);

public sealed record BackupFromCommitRequest(
    string? CommitSha,
    bool IncludeMetadata,
    bool IncludeDatabaseSnapshot);

public sealed record BackupDto(
    string Id,
    string FileName,
    string FilePath,
    long SizeBytes,
    DateTimeOffset CreatedAt);

public sealed record BackupCreateResult(
    string Id,
    string FileName,
    string FilePath,
    string DownloadUrl,
    long SizeBytes,
    IReadOnlyList<string> Warnings);

public sealed record RestoreAuditCreate(
    Guid WorkspaceId,
    Guid EnvironmentId,
    string EnvironmentKey,
    string RestoreType,
    string SourceCommitSha,
    string? NewCommitSha,
    string? FilePath,
    string Status,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
