namespace Application.Models;

public sealed record GitCommitResult(
    bool CommitCreated,
    string? CommitSha,
    int ChangedFilesCount,
    string Message,
    string? CommitMessage);
