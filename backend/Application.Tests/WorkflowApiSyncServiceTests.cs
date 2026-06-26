using System.Net;
using Application;
using Application.Contracts;
using Application.Models;
using Domain;
using Infrastructure;
using Xunit;

namespace Application.Tests;

public sealed class WorkflowApiSyncServiceTests
{
    [Fact]
    public async Task SyncSelected_ImportsFetchedWorkflowsThroughSharedImporter()
    {
        var importer = new FakeWorkflowImportService
        {
            Result = new UploadResultDto(1, 1, "commit-123", "Sync", "Workflow import committed.", [], 3)
        };
        var http = new FakeHttpClientFactory(new Queue<(HttpStatusCode Status, string Content)>([
            (HttpStatusCode.OK, """{"data":[{"id":"wf-1","name":"Remote Workflow"}],"nextCursor":null}"""),
            (HttpStatusCode.OK, """{"id":"wf-1","name":"Remote Workflow","nodes":[],"connections":{}}""")
        ]));
        var service = new WorkflowApiSyncService(
            new FakeEnvironmentService(),
            new FakeGitRepositoryService(),
            new FakeN8nApiConfigStore(),
            importer,
            new WorkflowNormalizer(),
            new WorkflowSemanticDiffService(),
            http);

        var result = await service.SyncSelectedAsync("local", ["wf-1"], CancellationToken.None);

        Assert.Equal("commit-123", result.CommitSha);
        Assert.Equal(1, result.FetchedWorkflowsCount);
        Assert.Equal("local", importer.EnvironmentKey);
        Assert.Single(importer.Sources);
        Assert.Equal("n8n-api-wf-1.json", importer.Sources[0].FileName);
        Assert.Contains("\"nodes\"", importer.Sources[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sync workflows from n8n API", importer.CommitMessage);
    }

    [Fact]
    public async Task Preview_ReturnsSemanticChangePreviewWithoutImporting()
    {
        var importer = new FakeWorkflowImportService();
        var git = new FakeGitRepositoryService
        {
            BranchFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["workflows/Remote-Workflow.json"] = """
                {
                  "active": false,
                  "connections": {},
                  "id": "wf-1",
                  "name": "Remote Workflow",
                  "nodes": []
                }
                """
            }
        };
        var http = new FakeHttpClientFactory(new Queue<(HttpStatusCode Status, string Content)>([
            (HttpStatusCode.OK, """{"data":[{"id":"wf-1","name":"Remote Workflow"}],"nextCursor":null}"""),
            (HttpStatusCode.OK, """{"id":"wf-1","name":"Remote Workflow","active":true,"nodes":[{"id":"node-1","name":"Webhook","type":"n8n-nodes-base.webhook"}],"connections":{}}""")
        ]));
        var service = new WorkflowApiSyncService(
            new FakeEnvironmentService(),
            git,
            new FakeN8nApiConfigStore(),
            importer,
            new WorkflowNormalizer(),
            new WorkflowSemanticDiffService(),
            http);

        var preview = await service.PreviewAsync("local", CancellationToken.None);

        Assert.Single(preview.Items);
        Assert.Equal("changed-remote", preview.Items[0].Status);
        Assert.Single(preview.ChangePreview.Workflows);
        Assert.Equal("modified", preview.ChangePreview.Workflows[0].ChangeType);
        Assert.Empty(importer.Sources);
    }

    private sealed class FakeEnvironmentService : IEnvironmentService
    {
        private readonly Workspace _workspace = new() { Id = Guid.NewGuid(), Name = "Test", RepoPath = Path.GetTempPath() };
        private readonly EnvironmentDefinition _environment;

        public FakeEnvironmentService()
        {
            _environment = new EnvironmentDefinition
            {
                Id = Guid.NewGuid(),
                WorkspaceId = _workspace.Id,
                Name = "Local",
                Key = "local",
                GitBranch = "env/local"
            };
        }

        public Task<EnvironmentContext> GetByKeyAsync(string environmentKey, CancellationToken cancellationToken) =>
            Task.FromResult(new EnvironmentContext(_workspace, _environment));

        public Task<IReadOnlyList<EnvironmentDto>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentDto> CreateAsync(EnvironmentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentDto> UpdateAsync(string environmentKey, EnvironmentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentClearResult> ClearAsync(string environmentKey, string? commitMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentDeleteResult> DeleteAsync(string environmentKey, bool force, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeN8nApiConfigStore : IEnvironmentN8nApiConfigStore
    {
        public Task<EnvironmentN8nApiConfigDto> GetAsync(string environmentKey, CancellationToken cancellationToken) =>
            Task.FromResult(new EnvironmentN8nApiConfigDto(
                Guid.NewGuid(),
                environmentKey,
                true,
                "https://n8n.example",
                "/api/v1/data-tables",
                null,
                "/api/v1/workflows",
                true,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));

        public Task<string?> GetApiKeyAsync(string environmentKey, CancellationToken cancellationToken) => Task.FromResult<string?>("api-key");

        public Task<EnvironmentN8nApiConfigDto> SaveAsync(string environmentKey, EnvironmentN8nApiConfigRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWorkflowImportService : IWorkflowImportService
    {
        public UploadResultDto Result { get; set; } = new(1, 1, "commit", "Sync", "Workflow import committed.", [], 0);
        public string EnvironmentKey { get; private set; } = string.Empty;
        public string? CommitMessage { get; private set; }
        public List<WorkflowUploadSource> Sources { get; } = [];

        public Task<UploadResultDto> ImportAsync(string environmentKey, IReadOnlyCollection<WorkflowUploadSource> sources, string? commitMessage, CancellationToken cancellationToken)
        {
            EnvironmentKey = environmentKey;
            CommitMessage = commitMessage;
            Sources.AddRange(sources);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeHttpClientFactory(Queue<(HttpStatusCode Status, string Content)> responses) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FakeHttpMessageHandler(responses));
    }

    private sealed class FakeHttpMessageHandler(Queue<(HttpStatusCode Status, string Content)> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.Contains("X-N8N-API-KEY"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            var response = responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Content)
            });
        }
    }

    private sealed class FakeGitRepositoryService : IGitRepositoryService
    {
        public IReadOnlyDictionary<string, string> BranchFiles { get; set; } = new Dictionary<string, string>();

        public void EnsureRepository(string repoPath) { }
        public void EnsureBranch(string repoPath, string branchName) { }
        public Task<GitCommitResult> CommitChangesAsync(string repoPath, string environmentKey, IReadOnlyCollection<string> relativePaths, string? commitMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public GitCommitDto AmendCommitMessage(string repoPath, string branchName, string commitSha, string commitMessage) => throw new NotSupportedException();
        public IReadOnlyList<GitCommitDto> GetRecentCommits(string repoPath, string branchName, int limit) => [];
        public GitCommitDto? GetCommit(string repoPath, string commitSha) => null;
        public IReadOnlyList<GitDiffFileDto> GetCommitDiff(string repoPath, string commitSha) => [];
        public IReadOnlyList<GitDiffFileDto> GetLatestDiff(string repoPath, string branchName) => [];
        public IReadOnlyList<GitDiffFileDto> CompareBranches(string repoPath, string sourceBranchName, string targetBranchName) => [];
        public IReadOnlyDictionary<string, string> ReadWorkflowFilesFromBranch(string repoPath, string branchName) => BranchFiles;
        public IReadOnlyDictionary<string, string> ReadWorkflowFilesFromCommit(string repoPath, string commitSha, bool parent) => new Dictionary<string, string>();
        public string? ReadWorkflowFileFromCommit(string repoPath, string commitSha, string filePath) => null;
        public IReadOnlyDictionary<string, string> ReadWorkflowFilesFromMergeBase(string repoPath, string sourceBranchName, string targetBranchName) => new Dictionary<string, string>();
        public GitMergeBaseInfo GetMergeBaseInfo(string repoPath, string sourceBranchName, string targetBranchName) => new(null, null, null);
        public Task<GitCommitResult> CommitPromotionChangesAsync(string repoPath, string sourceEnvironmentKey, string targetEnvironmentKey, IReadOnlyCollection<string> relativePaths, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
