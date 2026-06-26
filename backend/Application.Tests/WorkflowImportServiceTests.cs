using Application.Contracts;
using Application.Models;
using Domain;
using Infrastructure;
using Xunit;

namespace Application.Tests;

public sealed class WorkflowImportServiceTests : IDisposable
{
    private const string ManualKey = "manual";
    private const string ApiKey = "api";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "n8n-mm-import-tests", Guid.NewGuid().ToString("N"));
    private readonly string _repoPath;
    private readonly Workspace _workspace;
    private readonly EnvironmentDefinition _manualEnvironment;
    private readonly EnvironmentDefinition _apiEnvironment;
    private readonly GitRepositoryService _git = new();
    private readonly CapturingWorkflowMetadataService _metadata = new();
    private readonly CapturingCredentialInventoryService _credentials = new();

    public WorkflowImportServiceTests()
    {
        _repoPath = Path.Combine(_root, "repo");
        _workspace = new Workspace { Id = Guid.NewGuid(), Name = "Test", RepoPath = _repoPath };
        _manualEnvironment = new EnvironmentDefinition { Id = Guid.NewGuid(), WorkspaceId = _workspace.Id, Name = "Manual", Key = ManualKey, GitBranch = "env/manual" };
        _apiEnvironment = new EnvironmentDefinition { Id = Guid.NewGuid(), WorkspaceId = _workspace.Id, Name = "API", Key = ApiKey, GitBranch = "env/api" };
        _git.EnsureRepository(_repoPath);
    }

    [Fact]
    public async Task ManualUploadAndApiSyncSourcesCreateEquivalentWorkflowSnapshots()
    {
        var workflowJson = """
        {
          "updatedAt": "2026-06-26T09:00:00.000Z",
          "id": "wf-1",
          "name": "Move Candidate",
          "active": true,
          "nodes": [
            {
              "id": "node-1",
              "name": "Fetch Customer",
              "type": "n8n-nodes-base.httpRequest",
              "parameters": { "url": "https://example.test/customer" },
              "credentials": { "httpHeaderAuth": { "id": "cred-1", "name": "Customer API", "type": "httpHeaderAuth" } }
            }
          ],
          "connections": {},
          "createdAt": "2026-06-25T09:00:00.000Z",
          "versionId": "remote-version",
          "pinData": { "Fetch Customer": [] }
        }
        """;
        var service = CreateService();

        var manual = await service.ImportAsync(
            ManualKey,
            [new WorkflowUploadSource("manual-upload.json", workflowJson)],
            "Manual upload",
            CancellationToken.None);
        var api = await service.ImportAsync(
            ApiKey,
            [new WorkflowUploadSource("n8n-api-wf-1.json", workflowJson)],
            "API sync",
            CancellationToken.None);

        var manualFiles = _git.ReadWorkflowFilesFromBranch(_repoPath, _manualEnvironment.GitBranch);
        var apiFiles = _git.ReadWorkflowFilesFromBranch(_repoPath, _apiEnvironment.GitBranch);
        var manualMetadata = _metadata.Updates.Single(update => update.EnvironmentKey == ManualKey);
        var apiMetadata = _metadata.Updates.Single(update => update.EnvironmentKey == ApiKey);
        var manualCredentialReferences = _credentials.Replacements.Single(update => update.EnvironmentKey == ManualKey).References;
        var apiCredentialReferences = _credentials.Replacements.Single(update => update.EnvironmentKey == ApiKey).References;

        Assert.Equal(manual.ImportedWorkflowsCount, api.ImportedWorkflowsCount);
        Assert.True(manual.ChangedFilesCount > 0);
        Assert.True(api.ChangedFilesCount >= 0);
        Assert.Equal(manual.CredentialReferencesScanned, api.CredentialReferencesScanned);
        Assert.Equal(["workflows/Move-Candidate.json"], manualFiles.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(manualFiles.Keys, apiFiles.Keys);
        Assert.Equal(manualFiles["workflows/Move-Candidate.json"], apiFiles["workflows/Move-Candidate.json"]);
        Assert.DoesNotContain("versionId", manualFiles["workflows/Move-Candidate.json"]);
        Assert.DoesNotContain("pinData", manualFiles["workflows/Move-Candidate.json"]);
        Assert.Equal(
            manualMetadata with { WorkspaceId = Guid.Empty, EnvironmentId = Guid.Empty, EnvironmentKey = string.Empty, LastImportedAt = DateTimeOffset.MinValue },
            apiMetadata with { WorkspaceId = Guid.Empty, EnvironmentId = Guid.Empty, EnvironmentKey = string.Empty, LastImportedAt = DateTimeOffset.MinValue });
        Assert.Equal(
            manualCredentialReferences.Select(NormalizeReference).ToArray(),
            apiCredentialReferences.Select(NormalizeReference).ToArray());
    }

    private WorkflowImportService CreateService() =>
        new(
            new FakeEnvironmentService(_workspace, _manualEnvironment, _apiEnvironment),
            _metadata,
            _credentials,
            _git,
            new WorkflowNormalizer(),
            new WorkflowCredentialScanner());

    private static CredentialScanItem NormalizeReference(CredentialScanItem reference) =>
        reference with { WorkflowFilePath = "workflows/Move-Candidate.json" };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FakeEnvironmentService(
        Workspace workspace,
        EnvironmentDefinition manualEnvironment,
        EnvironmentDefinition apiEnvironment) : IEnvironmentService
    {
        public Task<EnvironmentContext> GetByKeyAsync(string environmentKey, CancellationToken cancellationToken) =>
            environmentKey switch
            {
                ManualKey => Task.FromResult(new EnvironmentContext(workspace, manualEnvironment)),
                ApiKey => Task.FromResult(new EnvironmentContext(workspace, apiEnvironment)),
                _ => throw new WorkflowImportException($"Unknown environment '{environmentKey}'.")
            };

        public Task<IReadOnlyList<EnvironmentDto>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentDto> CreateAsync(EnvironmentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentDto> UpdateAsync(string environmentKey, EnvironmentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentClearResult> ClearAsync(string environmentKey, string? commitMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentDeleteResult> DeleteAsync(string environmentKey, bool force, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapturingWorkflowMetadataService : IWorkflowMetadataService
    {
        public List<WorkflowMetadataUpdate> Updates { get; } = [];

        public Task UpsertAsync(WorkflowMetadataUpdate update, CancellationToken cancellationToken)
        {
            Updates.Add(update);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkflowListItemDto>> ListAsync(string environmentKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingCredentialInventoryService : ICredentialInventoryService
    {
        public List<(Guid WorkspaceId, Guid EnvironmentId, string EnvironmentKey, string WorkflowFilePath, IReadOnlyCollection<CredentialScanItem> References)> Replacements { get; } = [];

        public Task ReplaceWorkflowReferencesAsync(Guid workspaceId, Guid environmentId, string environmentKey, string workflowFilePath, IReadOnlyCollection<CredentialScanItem> references, CancellationToken cancellationToken)
        {
            Replacements.Add((workspaceId, environmentId, environmentKey, workflowFilePath, references));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EnvironmentCredentialDto>> ListEnvironmentCredentialsAsync(string environmentKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CredentialReferenceDto>> ListCredentialReferencesAsync(string environmentKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
