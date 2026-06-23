namespace Application.Models;

public sealed record UploadResultDto(
    int ImportedWorkflowsCount,
    int ChangedFilesCount,
    string? CommitSha,
    string? CommitMessage,
    string Message,
    IReadOnlyList<WorkflowImportItemDto> Workflows,
    int CredentialReferencesScanned);
