using Application.Models;

namespace Application.Contracts;

public interface IGitRepositoryService
{
    void EnsureRepository(string repoPath);
    void EnsureBranch(string repoPath, string branchName);
    Task<GitCommitResult> CommitChangesAsync(string repoPath, string environmentKey, IReadOnlyCollection<string> relativePaths, string? commitMessage, CancellationToken cancellationToken);
    GitCommitDto AmendCommitMessage(string repoPath, string branchName, string commitSha, string commitMessage);
    IReadOnlyList<GitCommitDto> GetRecentCommits(string repoPath, string branchName, int limit);
    GitCommitDto? GetCommit(string repoPath, string commitSha);
    IReadOnlyList<GitDiffFileDto> GetCommitDiff(string repoPath, string commitSha);
    IReadOnlyList<GitDiffFileDto> GetLatestDiff(string repoPath, string branchName);
    IReadOnlyList<GitDiffFileDto> CompareBranches(string repoPath, string sourceBranchName, string targetBranchName);
    IReadOnlyDictionary<string, string> ReadWorkflowFilesFromBranch(string repoPath, string branchName);
    IReadOnlyDictionary<string, string> ReadWorkflowFilesFromCommit(string repoPath, string commitSha, bool parent);
    string? ReadWorkflowFileFromCommit(string repoPath, string commitSha, string filePath);
    IReadOnlyDictionary<string, string> ReadWorkflowFilesFromMergeBase(string repoPath, string sourceBranchName, string targetBranchName);
    GitMergeBaseInfo GetMergeBaseInfo(string repoPath, string sourceBranchName, string targetBranchName);
    Task<GitCommitResult> CommitPromotionChangesAsync(
        string repoPath,
        string sourceEnvironmentKey,
        string targetEnvironmentKey,
        IReadOnlyCollection<string> changedRelativePaths,
        CancellationToken cancellationToken);
}
