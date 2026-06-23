using Application.Contracts;
using Application.Models;
using LibGit2Sharp;

namespace Infrastructure;

public sealed class GitRepositoryService : IGitRepositoryService
{
    private static readonly Signature Signature = new(
        "n8n Move Manager",
        "local@n8n-move-manager",
        DateTimeOffset.UtcNow);

    public void EnsureRepository(string repoPath)
    {
        Directory.CreateDirectory(repoPath);
        if (!Repository.IsValid(repoPath))
        {
            Repository.Init(repoPath);
        }
    }

    public void EnsureBranch(string repoPath, string branchName)
    {
        EnsureRepository(repoPath);
        using var repository = new Repository(repoPath);

        if (repository.Head.FriendlyName == branchName)
        {
            return;
        }

        var branch = repository.Branches[branchName];
        if (branch is not null)
        {
            Commands.Checkout(repository, branch);
            return;
        }

        if (repository.Head.Tip is null)
        {
            var headPath = Path.Combine(repository.Info.Path, "HEAD");
            File.WriteAllText(headPath, $"ref: refs/heads/{branchName.Replace('\\', '/')}{Environment.NewLine}");
            return;
        }

        branch = repository.CreateBranch(branchName, repository.Head.Tip);
        Commands.Checkout(repository, branch);
    }

    public Task<GitCommitResult> CommitChangesAsync(string repoPath, string environmentKey, IReadOnlyCollection<string> relativePaths, string? commitMessage, CancellationToken cancellationToken)
    {
        using var repository = new Repository(repoPath);

        foreach (var path in relativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Stage(repository, path.Replace('\\', '/'));
        }

        var status = repository.RetrieveStatus(new StatusOptions());
        var changedFiles = status
            .Where(entry => entry.State != FileStatus.Ignored && entry.State != FileStatus.Unaltered)
            .Select(entry => entry.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (changedFiles.Length == 0)
        {
            return Task.FromResult(new GitCommitResult(false, null, 0, "No changes were detected. No commit was created.", null));
        }

        var message = NormalizeCommitMessage(commitMessage, $"Import workflows into {environmentKey}: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}");
        var signature = new Signature(Signature.Name, Signature.Email, DateTimeOffset.UtcNow);
        var commit = repository.Commit(message, signature, signature);

        return Task.FromResult(new GitCommitResult(true, commit.Sha, changedFiles.Length, "Workflow import committed.", commit.MessageShort));
    }

    public GitCommitDto AmendCommitMessage(string repoPath, string branchName, string commitSha, string commitMessage)
    {
        using var repository = new Repository(repoPath);
        var branch = repository.Branches[branchName];
        if (branch?.Tip is null)
        {
            throw new InvalidOperationException($"Branch '{branchName}' does not have a commit to amend.");
        }

        if (!string.Equals(branch.Tip.Sha, commitSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only the latest commit on the selected environment branch can be renamed.");
        }

        Commands.Checkout(repository, branch);
        var signature = new Signature(Signature.Name, Signature.Email, DateTimeOffset.UtcNow);
        var amended = repository.Commit(
            NormalizeCommitMessage(commitMessage, branch.Tip.MessageShort),
            signature,
            signature,
            new CommitOptions { AmendPreviousCommit = true });

        return new GitCommitDto(
            amended.Sha,
            amended.Sha[..Math.Min(10, amended.Sha.Length)],
            amended.MessageShort,
            amended.Author.Name,
            amended.Author.Email,
            amended.Author.When);
    }

    public IReadOnlyList<GitCommitDto> GetRecentCommits(string repoPath, string branchName, int limit)
    {
        if (!Repository.IsValid(repoPath))
        {
            return [];
        }

        using var repository = new Repository(repoPath);
        var branch = repository.Branches[branchName];
        if (branch?.Tip is null)
        {
            return [];
        }

        return branch.Commits
            .Take(Math.Clamp(limit, 1, 100))
            .Select(commit => new GitCommitDto(
                commit.Sha,
                commit.Sha[..Math.Min(10, commit.Sha.Length)],
                commit.MessageShort,
                commit.Author.Name,
                commit.Author.Email,
                commit.Author.When))
            .ToArray();
    }

    public GitCommitDto? GetCommit(string repoPath, string commitSha)
    {
        if (!Repository.IsValid(repoPath))
        {
            return null;
        }

        using var repository = new Repository(repoPath);
        var commit = repository.Lookup<Commit>(commitSha);
        return commit is null
            ? null
            : new GitCommitDto(
                commit.Sha,
                commit.Sha[..Math.Min(10, commit.Sha.Length)],
                commit.MessageShort,
                commit.Author.Name,
                commit.Author.Email,
                commit.Author.When);
    }

    public IReadOnlyList<GitDiffFileDto> GetCommitDiff(string repoPath, string commitSha)
    {
        if (!Repository.IsValid(repoPath))
        {
            return [];
        }

        using var repository = new Repository(repoPath);
        var commit = repository.Lookup<Commit>(commitSha);
        if (commit is null)
        {
            return [];
        }

        var parent = commit.Parents.FirstOrDefault();
        return BuildPatch(repository, parent?.Tree, commit.Tree);
    }

    public IReadOnlyList<GitDiffFileDto> GetLatestDiff(string repoPath, string branchName)
    {
        if (!Repository.IsValid(repoPath))
        {
            return [];
        }

        using var repository = new Repository(repoPath);
        var commit = repository.Branches[branchName]?.Tip;
        if (commit is null)
        {
            return [];
        }

        var parent = commit.Parents.FirstOrDefault();
        return BuildPatch(repository, parent?.Tree, commit.Tree);
    }

    public IReadOnlyList<GitDiffFileDto> CompareBranches(string repoPath, string sourceBranchName, string targetBranchName)
    {
        if (!Repository.IsValid(repoPath))
        {
            return [];
        }

        using var repository = new Repository(repoPath);
        var source = repository.Branches[sourceBranchName]?.Tip;
        var target = repository.Branches[targetBranchName]?.Tip;
        if (source is null || target is null)
        {
            return [];
        }

        return BuildPatch(repository, target.Tree, source.Tree)
            .Where(file => file.FilePath.StartsWith("workflows/", StringComparison.OrdinalIgnoreCase)
                && file.FilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public IReadOnlyDictionary<string, string> ReadWorkflowFilesFromBranch(string repoPath, string branchName)
    {
        if (!Repository.IsValid(repoPath))
        {
            return new Dictionary<string, string>();
        }

        using var repository = new Repository(repoPath);
        var commit = repository.Branches[branchName]?.Tip;
        if (commit is null)
        {
            return new Dictionary<string, string>();
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ReadTree(repository, commit.Tree, string.Empty, files);
        return files;
    }

    public IReadOnlyDictionary<string, string> ReadWorkflowFilesFromCommit(string repoPath, string commitSha, bool parent)
    {
        if (!Repository.IsValid(repoPath))
        {
            return new Dictionary<string, string>();
        }

        using var repository = new Repository(repoPath);
        var commit = repository.Lookup<Commit>(commitSha);
        if (commit is null)
        {
            return new Dictionary<string, string>();
        }

        var snapshot = parent ? commit.Parents.FirstOrDefault() : commit;
        if (snapshot is null)
        {
            return new Dictionary<string, string>();
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ReadTree(repository, snapshot.Tree, string.Empty, files);
        return files;
    }

    public string? ReadWorkflowFileFromCommit(string repoPath, string commitSha, string filePath)
    {
        if (!Repository.IsValid(repoPath))
        {
            return null;
        }

        var normalizedPath = filePath.Replace('\\', '/');
        if (!IsWorkflowPath(normalizedPath))
        {
            return null;
        }

        using var repository = new Repository(repoPath);
        var commit = repository.Lookup<Commit>(commitSha);
        var entry = commit?[normalizedPath];
        if (entry?.TargetType != TreeEntryTargetType.Blob || entry.Target is not Blob blob)
        {
            return null;
        }

        using var content = blob.GetContentStream();
        using var reader = new StreamReader(content);
        return reader.ReadToEnd();
    }

    public IReadOnlyDictionary<string, string> ReadWorkflowFilesFromMergeBase(string repoPath, string sourceBranchName, string targetBranchName)
    {
        if (!Repository.IsValid(repoPath))
        {
            return new Dictionary<string, string>();
        }

        using var repository = new Repository(repoPath);
        var source = repository.Branches[sourceBranchName]?.Tip;
        var target = repository.Branches[targetBranchName]?.Tip;
        if (source is null || target is null)
        {
            return new Dictionary<string, string>();
        }

        var mergeBase = repository.ObjectDatabase.FindMergeBase(source, target);
        if (mergeBase is null)
        {
            return new Dictionary<string, string>();
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ReadTree(repository, mergeBase.Tree, string.Empty, files);
        return files;
    }

    public GitMergeBaseInfo GetMergeBaseInfo(string repoPath, string sourceBranchName, string targetBranchName)
    {
        if (!Repository.IsValid(repoPath))
        {
            return new GitMergeBaseInfo(null, null, null);
        }

        using var repository = new Repository(repoPath);
        var source = repository.Branches[sourceBranchName]?.Tip;
        var target = repository.Branches[targetBranchName]?.Tip;
        var mergeBase = source is null || target is null
            ? null
            : repository.ObjectDatabase.FindMergeBase(source, target);
        return new GitMergeBaseInfo(source?.Sha, target?.Sha, mergeBase?.Sha);
    }

    public Task<GitCommitResult> CommitPromotionChangesAsync(
        string repoPath,
        string sourceEnvironmentKey,
        string targetEnvironmentKey,
        IReadOnlyCollection<string> changedRelativePaths,
        CancellationToken cancellationToken)
    {
        using var repository = new Repository(repoPath);

        foreach (var path in changedRelativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Stage(repository, path.Replace('\\', '/'));
        }

        var status = repository.RetrieveStatus(new StatusOptions());
        var changedFiles = status
            .Where(entry => entry.State != FileStatus.Ignored && entry.State != FileStatus.Unaltered)
            .Select(entry => entry.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (changedFiles.Length == 0)
        {
            return Task.FromResult(new GitCommitResult(false, null, 0, "No changes were detected. No promotion commit was created.", null));
        }

        var message = $"Promote workflows from {sourceEnvironmentKey} to {targetEnvironmentKey}: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}";
        var signature = new Signature(Signature.Name, Signature.Email, DateTimeOffset.UtcNow);
        var commit = repository.Commit(message, signature, signature);

        return Task.FromResult(new GitCommitResult(true, commit.Sha, changedFiles.Length, "Promotion committed.", commit.MessageShort));
    }

    private static string NormalizeCommitMessage(string? requested, string fallback)
    {
        var message = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
        return message.Length <= 200 ? message : message[..200];
    }

    private static IReadOnlyList<GitDiffFileDto> BuildPatch(Repository repository, Tree? oldTree, Tree newTree)
    {
        var patch = repository.Diff.Compare<Patch>(oldTree, newTree);
        return patch
            .Select(entry => new GitDiffFileDto(
                entry.Path,
                MapStatus(entry.Status),
                entry.LinesAdded,
                entry.LinesDeleted,
                entry.Patch))
            .ToArray();
    }

    private static string MapStatus(ChangeKind status)
    {
        return status switch
        {
            ChangeKind.Added => "added",
            ChangeKind.Deleted => "deleted",
            ChangeKind.Modified => "modified",
            ChangeKind.Renamed => "modified",
            ChangeKind.Copied => "added",
            _ => "modified"
        };
    }

    private static void ReadTree(Repository repository, Tree tree, string prefix, IDictionary<string, string> files)
    {
        foreach (var entry in tree)
        {
            var path = string.IsNullOrWhiteSpace(prefix) ? entry.Name : $"{prefix}/{entry.Name}";
            if (entry.TargetType == TreeEntryTargetType.Tree && entry.Target is Tree child)
            {
                ReadTree(repository, child, path, files);
                continue;
            }

            if (!IsWorkflowPath(path)
                || entry.TargetType != TreeEntryTargetType.Blob
                || entry.Target is not Blob blob)
            {
                continue;
            }

            using var content = blob.GetContentStream();
            using var reader = new StreamReader(content);
            files[path] = reader.ReadToEnd();
        }
    }

    private static bool IsWorkflowPath(string path) =>
        path.StartsWith("workflows/", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && !path.Split('/').Any(segment => segment is "." or ".." || string.IsNullOrWhiteSpace(segment));
}
