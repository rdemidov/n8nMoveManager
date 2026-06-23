using System.IO.Compression;
using Application;
using Application.Contracts;
using Application.Models;
using Domain;
using Infrastructure;
using Xunit;

namespace Application.Tests;

public sealed class BackupRestoreServiceTests : IDisposable
{
    private const string PathA = "workflows/a.json";
    private const string PathB = "workflows/b.json";
    private const string PathC = "workflows/c.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "n8n-mm-restore-tests", Guid.NewGuid().ToString("N"));
    private readonly string _repoPath;
    private readonly string _appDataPath;
    private readonly GitRepositoryService _git = new();

    public BackupRestoreServiceTests()
    {
        _repoPath = Path.Combine(_root, "repo");
        _appDataPath = Path.Combine(_root, "App_Data");
        _git.EnsureRepository(_repoPath);
        _git.EnsureBranch(_repoPath, "env/local");
    }

    [Fact]
    public async Task ReadsWorkflowFileContentFromCommit()
    {
        var first = await WriteAndCommitAsync((PathA, Workflow("A", "old")));
        await WriteAndCommitAsync((PathA, Workflow("A", "new")));
        var service = CreateService();

        var file = await service.GetCommitFileContentAsync("local", first.CommitSha!, PathA, CancellationToken.None);

        Assert.Equal(PathA, file.FilePath);
        Assert.Contains("\"marker\": \"old\"", file.Content);
    }

    [Fact]
    public async Task ListsCommitFilesForDownloadBrowser()
    {
        var first = await WriteAndCommitAsync((PathA, Workflow("A", "old")), (PathB, Workflow("B", "old")));
        var service = CreateService();

        var files = await service.ListCommitFilesAsync("local", first.CommitSha!, CancellationToken.None);

        Assert.Contains(files, file => file.FilePath == PathA && file.SizeBytes > 0);
        Assert.Contains(files, file => file.FilePath == PathB && file.FileName == "b.json");
    }

    [Fact]
    public async Task DownloadableCommitFileContentUsesSelectedCommitVersion()
    {
        var first = await WriteAndCommitAsync((PathA, Workflow("A", "downloadable-old")));
        await WriteAndCommitAsync((PathA, Workflow("A", "downloadable-current")));
        var service = CreateService();

        var file = await service.GetCommitFileContentAsync("local", first.CommitSha!, PathA, CancellationToken.None);
        var downloadBytes = System.Text.Encoding.UTF8.GetBytes(file.Content);

        Assert.Equal("a.json", Path.GetFileName(file.FilePath));
        Assert.Contains("\"marker\": \"downloadable-old\"", System.Text.Encoding.UTF8.GetString(downloadBytes));
        Assert.DoesNotContain("downloadable-current", System.Text.Encoding.UTF8.GetString(downloadBytes));
    }

    [Fact]
    public async Task RestoreSingleWorkflowCreatesNewCommit()
    {
        var first = await WriteAndCommitAsync((PathA, Workflow("A", "old")));
        await WriteAndCommitAsync((PathA, Workflow("A", "new")));
        var service = CreateService();

        var result = await service.RestoreWorkflowAsync(
            "local",
            new RestoreWorkflowRequest(first.CommitSha, PathA, true),
            CancellationToken.None);

        Assert.True(result.CommitCreated);
        Assert.NotNull(result.NewCommitSha);
        Assert.NotEqual(first.CommitSha, result.NewCommitSha);
        Assert.Contains("\"marker\": \"old\"", await File.ReadAllTextAsync(Path.Combine(_repoPath, "workflows", "a.json")));
        Assert.Equal("modified", result.SemanticDiff.ChangeType);
    }

    [Fact]
    public async Task RestorePreviewDetectsAddedModifiedAndDeletedFiles()
    {
        var selected = await WriteAndCommitAsync((PathA, Workflow("A", "selected")), (PathB, Workflow("B", "selected")));
        File.Delete(Path.Combine(_repoPath, "workflows", "b.json"));
        await WriteFileAsync(PathA, Workflow("A", "current"));
        await WriteFileAsync(PathC, Workflow("C", "current"));
        await _git.CommitChangesAsync(_repoPath, "local", [PathA, PathB, PathC], "Current", CancellationToken.None);
        var service = CreateService();

        var preview = await service.PreviewEnvironmentRestoreAsync("local", selected.CommitSha!, CancellationToken.None);

        Assert.Contains(PathB, preview.FilesToAdd);
        Assert.Contains(PathA, preview.FilesToModify);
        Assert.Contains(PathC, preview.FilesThatWouldBeDeleted);
    }

    [Fact]
    public async Task RestoreEnvironmentCreatesNewCommit()
    {
        var selected = await WriteAndCommitAsync((PathA, Workflow("A", "selected")), (PathB, Workflow("B", "selected")));
        File.Delete(Path.Combine(_repoPath, "workflows", "b.json"));
        await WriteFileAsync(PathA, Workflow("A", "current"));
        await WriteFileAsync(PathC, Workflow("C", "current"));
        await _git.CommitChangesAsync(_repoPath, "local", [PathA, PathB, PathC], "Current", CancellationToken.None);
        var service = CreateService();

        var result = await service.RestoreEnvironmentAsync(
            "local",
            new RestoreEnvironmentRequest(selected.CommitSha, true, true),
            CancellationToken.None);

        Assert.True(result.CommitCreated);
        Assert.Equal(1, result.DeletedFilesCount);
        Assert.True(File.Exists(Path.Combine(_repoPath, "workflows", "b.json")));
        Assert.False(File.Exists(Path.Combine(_repoPath, "workflows", "c.json")));
    }

    [Fact]
    public async Task ZipBackupFromCommitIncludesWorkflowFilesAndManifest()
    {
        var selected = await WriteAndCommitAsync((PathA, Workflow("A", "selected")));
        var service = CreateService();

        var result = await service.CreateBackupFromCommitAsync(
            "local",
            new BackupFromCommitRequest(selected.CommitSha, true, false),
            _appDataPath,
            CancellationToken.None);

        using var archive = ZipFile.OpenRead(result.FilePath);
        Assert.NotNull(archive.GetEntry(PathA));
        Assert.NotNull(archive.GetEntry("backup-manifest.json"));
    }

    [Fact]
    public void GitRestoreImplementationDoesNotUseDestructiveReset()
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Infrastructure", "GitRepositoryService.cs")));

        Assert.DoesNotContain("ResetMode.Hard", source);
        Assert.DoesNotContain("git reset --hard", source, StringComparison.OrdinalIgnoreCase);
    }

    private BackupRestoreService CreateService()
    {
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Test", RepoPath = _repoPath };
        var environment = new EnvironmentDefinition
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            Name = "Local",
            Key = "local",
            GitBranch = "env/local"
        };

        return new BackupRestoreService(
            new FakeEnvironmentService(workspace, environment),
            new FakeWorkflowMetadataService(),
            new FakeCredentialInventoryService(),
            _git,
            new FakeRestoreAuditService(),
            new WorkflowNormalizer(),
            new WorkflowCredentialScanner(),
            new WorkflowSemanticDiffService());
    }

    private async Task<GitCommitResult> WriteAndCommitAsync(params (string Path, string Content)[] files)
    {
        foreach (var file in files)
        {
            await WriteFileAsync(file.Path, file.Content);
        }

        return await _git.CommitChangesAsync(_repoPath, "local", files.Select(file => file.Path).ToArray(), "Commit", CancellationToken.None);
    }

    private async Task WriteFileAsync(string relativePath, string content)
    {
        var path = Path.Combine(_repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private static string Workflow(string name, string marker)
    {
        return $$"""
        {
          "id": "{{name.ToLowerInvariant()}}",
          "name": "{{name}}",
          "nodes": [
            {
              "id": "node-1",
              "name": "Set",
              "type": "n8n-nodes-base.set",
              "parameters": { "marker": "{{marker}}" }
            }
          ],
          "connections": {}
        }
        """;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class FakeEnvironmentService : IEnvironmentService
    {
        private readonly Workspace _workspace;
        private readonly EnvironmentDefinition _environment;

        public FakeEnvironmentService(Workspace workspace, EnvironmentDefinition environment)
        {
            _workspace = workspace;
            _environment = environment;
        }

        public Task<IReadOnlyList<EnvironmentDto>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EnvironmentDto>>([]);
        public Task<EnvironmentContext> GetByKeyAsync(string environmentKey, CancellationToken cancellationToken) => Task.FromResult(new EnvironmentContext(_workspace, _environment));
        public Task<EnvironmentDto> CreateAsync(EnvironmentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentDto> UpdateAsync(string environmentKey, EnvironmentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentClearResult> ClearAsync(string environmentKey, string? commitMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentDeleteResult> DeleteAsync(string environmentKey, bool force, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeWorkflowMetadataService : IWorkflowMetadataService
    {
        public Task UpsertAsync(WorkflowMetadataUpdate update, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkflowListItemDto>> ListAsync(string environmentKey, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WorkflowListItemDto>>([]);
    }

    private sealed class FakeCredentialInventoryService : ICredentialInventoryService
    {
        public Task ReplaceWorkflowReferencesAsync(Guid workspaceId, Guid environmentId, string environmentKey, string workflowFilePath, IReadOnlyCollection<CredentialScanItem> references, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<EnvironmentCredentialDto>> ListEnvironmentCredentialsAsync(string environmentKey, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EnvironmentCredentialDto>>([]);
        public Task<IReadOnlyList<CredentialReferenceDto>> ListCredentialReferencesAsync(string environmentKey, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CredentialReferenceDto>>([]);
    }

    private sealed class FakeRestoreAuditService : IRestoreAuditService
    {
        public Task RecordAsync(RestoreAuditCreate audit, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
