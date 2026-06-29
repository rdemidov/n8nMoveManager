using Application.Contracts;
using Application.Models;
using Domain;
using Xunit;

namespace Application.Tests;

public sealed class PromotionServiceMergeTests
{
    private const string PathA = "workflows/workflow-a.json";
    private readonly Guid _sourceId = Guid.NewGuid();
    private readonly Guid _targetId = Guid.NewGuid();

    [Fact]
    public async Task GeneratePlan_UsesDirectComparisonWithoutAConfiguredBaseline()
    {
        var service = CreateService(
            sourceFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "source") },
            targetFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "target") },
            baseFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "base") });

        var plan = await service.GeneratePlanAsync("dev", "prod", null, false, false, CancellationToken.None);
        var change = Assert.Single(plan.WorkflowChanges);

        Assert.False(change.IsConflict);
        Assert.Equal("different", change.ChangeType);
        Assert.Equal("use-source", change.Resolution);
        Assert.Null(change.ConflictDetails);
    }

    [Fact]
    public async Task GeneratePlan_MarksWorkflowConflict_WhenChangesDivergeFromConfiguredBaseline()
    {
        var service = CreateService(
            sourceFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "source") },
            targetFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "target") },
            baseFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "base") },
            hasBaseline: true);

        var plan = await service.GeneratePlanAsync("dev", "prod", null, false, false, CancellationToken.None);
        var change = Assert.Single(plan.WorkflowChanges);

        Assert.True(change.IsConflict);
        Assert.Null(change.Resolution);
        Assert.Equal("base-sha", change.ConflictDetails?.BaseCommitSha);
    }

    [Fact]
    public async Task Apply_BlocksUnresolvedConflict()
    {
        var service = CreateService(
            sourceFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "source") },
            targetFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "target") },
            baseFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "base") },
            hasBaseline: true);

        var ex = await Assert.ThrowsAsync<WorkflowImportException>(() => service.ApplyAsync(
            new PromotionApplyRequest("dev", "prod", null, null, true, false, false),
            CancellationToken.None));

        Assert.Contains("unresolved conflict", ex.Message);
    }

    [Fact]
    public async Task GeneratePlan_ShowsAppliedManualMergeAsResolvedTargetWorkflow()
    {
        var service = CreateService(
            sourceFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "source") },
            targetFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "manual-result") },
            baseFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "base") },
            appliedManualMerges: [new AppliedManualMergeAuditEntry(PathA, "manual-commit-sha", DateTimeOffset.UtcNow)]);

        var plan = await service.GeneratePlanAsync("dev", "prod", null, false, false, CancellationToken.None);
        var change = Assert.Single(plan.WorkflowChanges);

        Assert.Equal("manual-merge", change.ChangeType);
        Assert.False(change.IsConflict);
        Assert.Equal("keep-target", change.Resolution);
        Assert.Equal(["keep-target"], change.AvailableResolutions);
        Assert.Contains("Manual merge applied", change.Summary);
    }

    [Fact]
    public async Task MergePreview_CategorizesUseSourceKeepTargetSkipAndDeleteTarget()
    {
        var service = CreateService(
            sourceFiles: new Dictionary<string, string>
            {
                [PathA] = Workflow("Workflow A", "source"),
                ["workflows/workflow-b.json"] = Workflow("Workflow B", "source")
            },
            targetFiles: new Dictionary<string, string>
            {
                [PathA] = Workflow("Workflow A", "target"),
                ["workflows/workflow-b.json"] = Workflow("Workflow B", "target"),
                ["workflows/deleted.json"] = Workflow("Deleted", "target")
            },
            baseFiles: new Dictionary<string, string>
            {
                [PathA] = Workflow("Workflow A", "base"),
                ["workflows/workflow-b.json"] = Workflow("Workflow B", "base"),
                ["workflows/deleted.json"] = Workflow("Deleted", "target")
            });

        var preview = await service.PreviewMergeAsync(new PromotionMergePreviewRequest(
            "dev",
            "prod",
            [
                new PromotionWorkflowResolutionDto(PathA, "use-source"),
                new PromotionWorkflowResolutionDto("workflows/workflow-b.json", "skip"),
                new PromotionWorkflowResolutionDto("workflows/deleted.json", "delete-target")
            ],
            true,
            true), CancellationToken.None);

        Assert.Contains(PathA, preview.WorkflowsToWrite);
        Assert.Contains("workflows/workflow-b.json", preview.WorkflowsToSkip);
        Assert.Contains("workflows/deleted.json", preview.WorkflowsToDelete);
        Assert.Empty(preview.BlockingErrors);
    }

    [Fact]
    public async Task MergePreview_BlocksUseSource_WhenCredentialMappingMissing()
    {
        var workflow = Workflow("Workflow", "source", """
        "credentials": { "httpBasicAuth": { "id": "source-cred", "name": "Source credential", "type": "httpBasicAuth" } },
        """);
        var service = CreateService(
            sourceFiles: new Dictionary<string, string> { [PathA] = workflow },
            targetFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "target") },
            baseFiles: new Dictionary<string, string> { [PathA] = Workflow("Workflow", "base") });

        var preview = await service.PreviewMergeAsync(new PromotionMergePreviewRequest(
            "dev",
            "prod",
            [new PromotionWorkflowResolutionDto(PathA, "use-source")],
            false,
            false), CancellationToken.None);

        Assert.Contains(preview.BlockingErrors, error => error.Contains("Missing mapping", StringComparison.OrdinalIgnoreCase));
    }

    private PromotionService CreateService(
        IReadOnlyDictionary<string, string> sourceFiles,
        IReadOnlyDictionary<string, string> targetFiles,
        IReadOnlyDictionary<string, string> baseFiles,
        IReadOnlyList<CredentialEnvironmentPair>? mappings = null,
        IReadOnlyList<AppliedManualMergeAuditEntry>? appliedManualMerges = null,
        bool hasBaseline = false)
    {
        return new PromotionService(
            new FakeEnvironmentService(_sourceId, _targetId),
            new FakeGitRepositoryService(sourceFiles, targetFiles, baseFiles),
            new FakeCredentialMappingReader(mappings ?? []),
            new FakePromotionAuditService(appliedManualMerges ?? []),
            new FakePromotionBaselineService(hasBaseline),
            new FakeWorkflowMetadataService(),
            new FakeCredentialInventoryService(),
            new WorkflowCredentialScanner(),
            new WorkflowNormalizer(),
            new WorkflowSemanticDiffService());
    }

    private static string Workflow(string name, string marker, string extraNodeFields = "")
    {
        return $$"""
        {
          "id": "{{name.Replace(" ", "-").ToLowerInvariant()}}",
          "name": "{{name}}",
          "nodes": [
            {
              "id": "node-1",
              "name": "Set",
              "type": "n8n-nodes-base.set",
              {{extraNodeFields}}
              "parameters": { "marker": "{{marker}}" }
            }
          ],
          "connections": {}
        }
        """;
    }

    private sealed class FakeEnvironmentService : IEnvironmentService
    {
        private readonly Workspace _workspace = new() { Id = Guid.NewGuid(), Name = "Test", RepoPath = "repo" };
        private readonly EnvironmentDefinition _source;
        private readonly EnvironmentDefinition _target;

        public FakeEnvironmentService(Guid sourceId, Guid targetId)
        {
            _source = new EnvironmentDefinition { Id = sourceId, WorkspaceId = _workspace.Id, Name = "Dev", Key = "dev", GitBranch = "env/dev" };
            _target = new EnvironmentDefinition { Id = targetId, WorkspaceId = _workspace.Id, Name = "Prod", Key = "prod", GitBranch = "env/prod" };
        }

        public Task<IReadOnlyList<EnvironmentDto>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EnvironmentDto>>([]);
        public Task<EnvironmentContext> GetByKeyAsync(string environmentKey, CancellationToken cancellationToken) => Task.FromResult(new EnvironmentContext(_workspace, environmentKey == "dev" ? _source : _target));
        public Task<EnvironmentDto> CreateAsync(EnvironmentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentDto> UpdateAsync(string environmentKey, EnvironmentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentClearResult> ClearAsync(string environmentKey, string? commitMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentDeleteResult> DeleteAsync(string environmentKey, bool force, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeGitRepositoryService : IGitRepositoryService
    {
        private readonly IReadOnlyDictionary<string, string> _sourceFiles;
        private readonly IReadOnlyDictionary<string, string> _targetFiles;
        private readonly IReadOnlyDictionary<string, string> _baseFiles;

        public FakeGitRepositoryService(IReadOnlyDictionary<string, string> sourceFiles, IReadOnlyDictionary<string, string> targetFiles, IReadOnlyDictionary<string, string> baseFiles)
        {
            _sourceFiles = sourceFiles;
            _targetFiles = targetFiles;
            _baseFiles = baseFiles;
        }

        public void EnsureRepository(string repoPath) { }
        public void EnsureBranch(string repoPath, string branchName) { }
        public Task<GitCommitResult> CommitChangesAsync(string repoPath, string environmentKey, IReadOnlyCollection<string> relativePaths, string? commitMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public GitCommitDto AmendCommitMessage(string repoPath, string branchName, string commitSha, string commitMessage) => throw new NotSupportedException();
        public GitCommitDto? GetCommit(string repoPath, string commitSha) => new(commitSha, commitSha[..Math.Min(10, commitSha.Length)], "Test", "Test", "test@example.com", DateTimeOffset.UtcNow, null);
        public IReadOnlyList<GitCommitDto> GetRecentCommits(string repoPath, string branchName, int limit) => [];
        public IReadOnlyList<GitDiffFileDto> GetCommitDiff(string repoPath, string commitSha) => [];
        public IReadOnlyList<GitDiffFileDto> GetLatestDiff(string repoPath, string branchName) => [];
        public IReadOnlyList<GitDiffFileDto> CompareBranches(string repoPath, string sourceBranchName, string targetBranchName) => [];
        public IReadOnlyDictionary<string, string> ReadWorkflowFilesFromBranch(string repoPath, string branchName) => branchName == "env/dev" ? _sourceFiles : _targetFiles;
        public IReadOnlyDictionary<string, string> ReadWorkflowFilesFromCommit(string repoPath, string commitSha, bool parent) => _baseFiles;
        public string? ReadWorkflowFileFromCommit(string repoPath, string commitSha, string filePath) => null;
        public IReadOnlyDictionary<string, string> ReadWorkflowFilesFromMergeBase(string repoPath, string sourceBranchName, string targetBranchName) => _baseFiles;
        public GitMergeBaseInfo GetMergeBaseInfo(string repoPath, string sourceBranchName, string targetBranchName) => new("source-sha", "target-sha", "base-sha");
        public Task<GitCommitResult> CommitPromotionChangesAsync(string repoPath, string sourceEnvironmentKey, string targetEnvironmentKey, IReadOnlyCollection<string> changedRelativePaths, CancellationToken cancellationToken) => Task.FromResult(new GitCommitResult(true, "commit-sha", changedRelativePaths.Count, "Promotion committed.", "Promotion"));
    }

    private sealed class FakeCredentialMappingReader : ICredentialMappingReader
    {
        private readonly IReadOnlyList<CredentialEnvironmentPair> _mappings;
        public FakeCredentialMappingReader(IReadOnlyList<CredentialEnvironmentPair> mappings) => _mappings = mappings;
        public Task<IReadOnlyList<CredentialEnvironmentPair>> GetMappingsAsync(Guid sourceEnvironmentId, Guid targetEnvironmentId, CancellationToken cancellationToken) => Task.FromResult(_mappings);
    }

    private sealed class FakePromotionAuditService : IPromotionAuditService
    {
        private readonly IReadOnlyList<AppliedManualMergeAuditEntry> _entries;

        public FakePromotionAuditService(IReadOnlyList<AppliedManualMergeAuditEntry> entries)
        {
            _entries = entries;
        }

        public Task RecordAsync(PromotionAuditCreate audit, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<AppliedManualMergeAuditEntry>> ListAppliedManualMergesAsync(Guid workspaceId, Guid sourceEnvironmentId, Guid targetEnvironmentId, CancellationToken cancellationToken) => Task.FromResult(_entries);
    }

    private sealed class FakePromotionBaselineService : IPromotionBaselineService
    {
        private readonly bool _hasBaseline;

        public FakePromotionBaselineService(bool hasBaseline)
        {
            _hasBaseline = hasBaseline;
        }

        public Task<PromotionComparisonBaselineDto?> GetAsync(string sourceEnvironmentKey, string targetEnvironmentKey, CancellationToken cancellationToken) => Task.FromResult<PromotionComparisonBaselineDto?>(
            _hasBaseline ? new PromotionComparisonBaselineDto("base-sha", "Baseline", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) : null);
        public Task<PromotionComparisonBaselineDto?> SetAsync(PromotionComparisonBaselineRequest request, CancellationToken cancellationToken) => Task.FromResult<PromotionComparisonBaselineDto?>(null);
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
}
