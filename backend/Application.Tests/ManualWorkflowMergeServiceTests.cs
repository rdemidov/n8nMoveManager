using Application;
using Application.Contracts;
using Application.Models;
using Domain;
using Infrastructure;
using Xunit;

namespace Application.Tests;

public sealed class ManualWorkflowMergeServiceTests : IDisposable
{
    private const string WorkflowPath = "workflows/manual.json";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "n8n-mm-manual-merge-tests", Guid.NewGuid().ToString("N"));
    private readonly string _repoPath;
    private readonly GitRepositoryService _git = new();
    private readonly Workspace _workspace;
    private readonly EnvironmentDefinition _sourceEnvironment;
    private readonly EnvironmentDefinition _targetEnvironment;
    private readonly FakeCredentialMappingReader _mappingReader = new();

    public ManualWorkflowMergeServiceTests()
    {
        _repoPath = Path.Combine(_root, "repo");
        _workspace = new Workspace { Id = Guid.NewGuid(), Name = "Test", RepoPath = _repoPath };
        _sourceEnvironment = new EnvironmentDefinition { Id = Guid.NewGuid(), WorkspaceId = _workspace.Id, Name = "Source", Key = "source", GitBranch = "env/source" };
        _targetEnvironment = new EnvironmentDefinition { Id = Guid.NewGuid(), WorkspaceId = _workspace.Id, Name = "Target", Key = "target", GitBranch = "env/target" };
        _git.EnsureRepository(_repoPath);
    }

    [Fact]
    public async Task CreateSessionBuildsDefaultSelectionsForAddedAndModifiedNodes()
    {
        await SeedAsync(
            source: Workflow("Source", string.Join(",", Node("shared", "Shared", "n8n-nodes-base.set", """{"value":"source"}"""), Node("added", "Added", "n8n-nodes-base.set", """{"value":"added"}"""))),
            target: Workflow("Target", Node("shared", "Shared", "n8n-nodes-base.set", """{"value":"target"}""")));
        var service = CreateService();

        var session = await service.CreateSessionAsync(Request(), CancellationToken.None);

        Assert.Contains(session.Selection.NodeSelections, node => node.NodeName == "Added" && node.Resolution == "use-source");
        Assert.Contains(session.Selection.NodeSelections, node => node.NodeName == "Shared" && node.Resolution == "parameter-level");
        Assert.Contains(session.Selection.ParameterSelections, parameter => parameter.ParameterPath == "value" && parameter.SelectedSide == "target");
    }

    [Fact]
    public async Task SelectingSourceNodeAddsItToResult()
    {
        await SeedAsync(
            source: Workflow("Source", Node("shared", "Shared", "n8n-nodes-base.set", """{"value":"source"}""")),
            target: Workflow("Target", Node("shared", "Shared", "n8n-nodes-base.set", """{"value":"target"}""")));
        var service = CreateService();
        var session = await service.CreateSessionAsync(Request(), CancellationToken.None);
        var selection = session.Selection with
        {
            NodeSelections = session.Selection.NodeSelections.Select(node => node with { Resolution = "use-source" }).ToArray()
        };
        await service.UpdateSelectionAsync(session.Id, new ManualMergeSelectionUpdateRequest(selection), CancellationToken.None);

        var preview = await service.PreviewAsync(session.Id, CancellationToken.None);

        Assert.Contains("\"value\": \"source\"", preview.ResultWorkflowJson);
    }

    [Fact]
    public async Task SelectingTargetNodeKeepsTargetValue()
    {
        await SeedAsync(
            source: Workflow("Source", Node("shared", "Shared", "n8n-nodes-base.set", """{"value":"source"}""")),
            target: Workflow("Target", Node("shared", "Shared", "n8n-nodes-base.set", """{"value":"target"}""")));
        var service = CreateService();
        var session = await service.CreateSessionAsync(Request(), CancellationToken.None);
        var selection = session.Selection with
        {
            NodeSelections = session.Selection.NodeSelections.Select(node => node with { Resolution = "use-target" }).ToArray()
        };
        await service.UpdateSelectionAsync(session.Id, new ManualMergeSelectionUpdateRequest(selection), CancellationToken.None);

        var preview = await service.PreviewAsync(session.Id, CancellationToken.None);

        Assert.Contains("\"value\": \"target\"", preview.ResultWorkflowJson);
    }

    [Fact]
    public async Task ExcludingNodeRemovesItAndCanBlockBrokenConnections()
    {
        await SeedAsync(
            source: Workflow("Source", string.Join(",", Node("a", "A", "n8n-nodes-base.set", """{"value":"a"}"""), Node("b", "B", "n8n-nodes-base.set", """{"value":"b"}""")), connections: """{"A":{"main":[[{"node":"B","type":"main","index":0}]]}}"""),
            target: Workflow("Target", string.Join(",", Node("a", "A", "n8n-nodes-base.set", """{"value":"a"}"""), Node("b", "B", "n8n-nodes-base.set", """{"value":"b"}""")), connections: """{"A":{"main":[[{"node":"B","type":"main","index":0}]]}}"""));
        var service = CreateService();
        var session = await service.CreateSessionAsync(Request(), CancellationToken.None);
        var selection = session.Selection with
        {
            ConnectionSelection = "target",
            NodeSelections = session.Selection.NodeSelections.Select(node => node.NodeName == "B" ? node with { Resolution = "exclude" } : node).ToArray()
        };
        await service.UpdateSelectionAsync(session.Id, new ManualMergeSelectionUpdateRequest(selection), CancellationToken.None);

        var preview = await service.PreviewAsync(session.Id, CancellationToken.None);

        Assert.Contains(preview.BlockingErrors, error => error.Contains("Connection target 'B'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SelectingSourceParameterUsesOnlyThatParameter()
    {
        await SeedAsync(
            source: Workflow("Source", Node("shared", "Shared", "n8n-nodes-base.set", """{"value":"source","keep":"source"}""")),
            target: Workflow("Target", Node("shared", "Shared", "n8n-nodes-base.set", """{"value":"target","keep":"target"}""")));
        var service = CreateService();
        var session = await service.CreateSessionAsync(Request(), CancellationToken.None);
        var selection = session.Selection with
        {
            ParameterSelections = session.Selection.ParameterSelections.Select(parameter =>
                parameter.ParameterPath == "value" ? parameter with { SelectedSide = "source" } : parameter with { SelectedSide = "target" }).ToArray()
        };
        await service.UpdateSelectionAsync(session.Id, new ManualMergeSelectionUpdateRequest(selection), CancellationToken.None);

        var preview = await service.PreviewAsync(session.Id, CancellationToken.None);

        Assert.Contains("\"value\": \"source\"", preview.ResultWorkflowJson);
        Assert.Contains("\"keep\": \"target\"", preview.ResultWorkflowJson);
    }

    [Fact]
    public async Task CredentialMappingIsAppliedToSelectedSourceNode()
    {
        var sourceCredential = new EnvironmentCredentialSnapshot(Guid.NewGuid(), _sourceEnvironment.Id, "apiKey", "source-id", "Source Key");
        var targetCredential = new EnvironmentCredentialSnapshot(Guid.NewGuid(), _targetEnvironment.Id, "apiKey", "target-id", "Target Key");
        _mappingReader.Mappings.Add(new CredentialEnvironmentPair("api", sourceCredential, targetCredential));
        await SeedAsync(
            source: Workflow("Source", Node("shared", "Shared", "n8n-nodes-base.httpRequest", """{"value":"source"}""", "\"credentials\":{\"apiKey\":{\"id\":\"source-id\",\"name\":\"Source Key\",\"type\":\"apiKey\"}}")),
            target: Workflow("Target", Node("shared", "Shared", "n8n-nodes-base.httpRequest", """{"value":"target"}""")));
        var service = CreateService();
        var session = await service.CreateSessionAsync(Request(), CancellationToken.None);
        var selection = session.Selection with
        {
            NodeSelections = session.Selection.NodeSelections.Select(node => node with { Resolution = "use-source" }).ToArray()
        };
        await service.UpdateSelectionAsync(session.Id, new ManualMergeSelectionUpdateRequest(selection), CancellationToken.None);

        var preview = await service.PreviewAsync(session.Id, CancellationToken.None);

        Assert.Empty(preview.BlockingErrors);
        Assert.Contains("\"id\": \"target-id\"", preview.ResultWorkflowJson);
        Assert.DoesNotContain("source-id", preview.ResultWorkflowJson);
    }

    [Fact]
    public async Task TargetMappedCredentialWithSameDisplayNameDoesNotBlockPreview()
    {
        var sourceCredential = new EnvironmentCredentialSnapshot(Guid.NewGuid(), _sourceEnvironment.Id, "slackApi", null, "Slack account");
        var targetCredential = new EnvironmentCredentialSnapshot(Guid.NewGuid(), _targetEnvironment.Id, "slackApi", null, "Slack account");
        _mappingReader.Mappings.Add(new CredentialEnvironmentPair("slack", sourceCredential, targetCredential));
        await SeedAsync(
            source: Workflow("Source", Node("shared", "Send a message", "n8n-nodes-base.slack", """{"value":"source"}""", "\"credentials\":{\"slackApi\":{\"name\":\"Slack account\",\"type\":\"slackApi\"}}")),
            target: Workflow("Target", Node("shared", "Send a message", "n8n-nodes-base.slack", """{"value":"target"}""")));
        var service = CreateService();
        var session = await service.CreateSessionAsync(Request(), CancellationToken.None);
        var selection = session.Selection with
        {
            NodeSelections = session.Selection.NodeSelections.Select(node => node with { Resolution = "use-source" }).ToArray()
        };
        await service.UpdateSelectionAsync(session.Id, new ManualMergeSelectionUpdateRequest(selection), CancellationToken.None);

        var preview = await service.PreviewAsync(session.Id, CancellationToken.None);

        Assert.DoesNotContain(preview.BlockingErrors, error => error.Contains("Source-only credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingCredentialMappingBlocksApply()
    {
        await SeedAsync(
            source: Workflow("Source", Node("shared", "Shared", "n8n-nodes-base.httpRequest", """{"value":"source"}""", "\"credentials\":{\"apiKey\":{\"id\":\"source-id\",\"name\":\"Source Key\",\"type\":\"apiKey\"}}")),
            target: Workflow("Target", Node("shared", "Shared", "n8n-nodes-base.httpRequest", """{"value":"target"}""")));
        var service = CreateService();
        var session = await service.CreateSessionAsync(Request(), CancellationToken.None);
        var selection = session.Selection with
        {
            NodeSelections = session.Selection.NodeSelections.Select(node => node with { Resolution = "use-source" }).ToArray()
        };
        await service.UpdateSelectionAsync(session.Id, new ManualMergeSelectionUpdateRequest(selection), CancellationToken.None);

        var preview = await service.PreviewAsync(session.Id, CancellationToken.None);

        Assert.Contains(preview.BlockingErrors, error => error.Contains("Missing target credential mapping", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<WorkflowImportException>(() => service.ApplyAsync(session.Id, new ManualMergeApplyRequest(true), CancellationToken.None));
    }

    [Fact]
    public async Task ResultWorkflowIsNormalized()
    {
        await SeedAsync(
            source: Workflow("Source", Node("shared", "Shared", "n8n-nodes-base.set", """{"value":"source"}"""), extra: "\"staticData\":{\"old\":true},\"versionId\":\"abc\","),
            target: Workflow("Target", Node("shared", "Shared", "n8n-nodes-base.set", """{"value":"target"}"""), extra: "\"pinData\":{\"x\":true},"));
        var service = CreateService();
        var session = await service.CreateSessionAsync(Request(), CancellationToken.None);

        var preview = await service.PreviewAsync(session.Id, CancellationToken.None);

        Assert.DoesNotContain("staticData", preview.ResultWorkflowJson);
        Assert.DoesNotContain("versionId", preview.ResultWorkflowJson);
        Assert.DoesNotContain("pinData", preview.ResultWorkflowJson);
    }

    [Fact]
    public async Task ApplyCreatesCommitOnTargetBranch()
    {
        await SeedAsync(
            source: Workflow("Source", Node("shared", "Shared", "n8n-nodes-base.set", """{"value":"source"}""")),
            target: Workflow("Target", Node("shared", "Shared", "n8n-nodes-base.set", """{"value":"target"}""")));
        var service = CreateService();
        var session = await service.CreateSessionAsync(Request(), CancellationToken.None);
        var selection = session.Selection with
        {
            ParameterSelections = session.Selection.ParameterSelections.Select(parameter => parameter with { SelectedSide = "source" }).ToArray()
        };
        await service.UpdateSelectionAsync(session.Id, new ManualMergeSelectionUpdateRequest(selection), CancellationToken.None);

        var result = await service.ApplyAsync(session.Id, new ManualMergeApplyRequest(true), CancellationToken.None);

        Assert.True(result.CommitCreated);
        Assert.NotNull(result.CommitSha);
        Assert.Contains("Manual merge", result.CommitMessage);
    }

    [Fact]
    public async Task ApplyExplainsWhenGeneratedResultAlreadyMatchesTarget()
    {
        var shared = Workflow("Shared", Node("shared", "Shared", "n8n-nodes-base.set", """{"value":"same"}"""));
        await SeedAsync(source: shared, target: shared);
        var service = CreateService();
        var session = await service.CreateSessionAsync(Request(), CancellationToken.None);

        var result = await service.ApplyAsync(session.Id, new ManualMergeApplyRequest(true), CancellationToken.None);

        Assert.False(result.CommitCreated);
        Assert.Contains("already matches the current target workflow", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private ManualMergeCreateRequest Request() => new("source", "target", WorkflowPath, null, null);

    private ManualWorkflowMergeService CreateService() =>
        new(
            new FakeEnvironmentService(_workspace, _sourceEnvironment, _targetEnvironment),
            _git,
            _mappingReader,
            new FakePromotionAuditService(),
            new FakePromotionBaselineService(),
            new FakeWorkflowMetadataService(),
            new FakeCredentialInventoryService(),
            new WorkflowCredentialScanner(),
            new WorkflowNormalizer(),
            new WorkflowSemanticDiffService(),
            new MergedWorkflowValidationService(new WorkflowCredentialScanner()));

    private async Task SeedAsync(string source, string target)
    {
        _git.EnsureBranch(_repoPath, _sourceEnvironment.GitBranch);
        await WriteFileAsync(source);
        await _git.CommitChangesAsync(_repoPath, "source", [WorkflowPath], "Source", CancellationToken.None);
        _git.EnsureBranch(_repoPath, _targetEnvironment.GitBranch);
        await WriteFileAsync(target);
        await _git.CommitChangesAsync(_repoPath, "target", [WorkflowPath], "Target", CancellationToken.None);
    }

    private async Task WriteFileAsync(string content)
    {
        var path = Path.Combine(_repoPath, WorkflowPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private static string Workflow(string name, params string[] nodes) =>
        Workflow(name, string.Join(",", nodes), connections: "{}", extra: string.Empty);

    private static string Workflow(string name, string nodes, string connections = "{}", string extra = "") =>
        $$"""
        {
          {{extra}}
          "id": "{{name.ToLowerInvariant()}}",
          "name": "{{name}}",
          "active": false,
          "nodes": [{{nodes}}],
          "connections": {{connections}}
        }
        """;

    private static string Node(string id, string name, string type, string parameters, string extra = "") =>
        $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "type": "{{type}}",
          "parameters": {{parameters}}{{(string.IsNullOrWhiteSpace(extra) ? string.Empty : "," + extra)}}
        }
        """;

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
        private readonly Dictionary<string, EnvironmentDefinition> _environments;

        public FakeEnvironmentService(Workspace workspace, params EnvironmentDefinition[] environments)
        {
            _workspace = workspace;
            _environments = environments.ToDictionary(environment => environment.Key, StringComparer.OrdinalIgnoreCase);
        }

        public Task<IReadOnlyList<EnvironmentDto>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EnvironmentDto>>([]);
        public Task<EnvironmentContext> GetByKeyAsync(string environmentKey, CancellationToken cancellationToken) => Task.FromResult(new EnvironmentContext(_workspace, _environments[environmentKey]));
        public Task<EnvironmentDto> CreateAsync(EnvironmentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentDto> UpdateAsync(string environmentKey, EnvironmentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentClearResult> ClearAsync(string environmentKey, string? commitMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EnvironmentDeleteResult> DeleteAsync(string environmentKey, bool force, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeCredentialMappingReader : ICredentialMappingReader
    {
        public List<CredentialEnvironmentPair> Mappings { get; } = [];
        public Task<IReadOnlyList<CredentialEnvironmentPair>> GetMappingsAsync(Guid sourceEnvironmentId, Guid targetEnvironmentId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CredentialEnvironmentPair>>(Mappings);
    }

    private sealed class FakePromotionAuditService : IPromotionAuditService
    {
        public Task RecordAsync(PromotionAuditCreate audit, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<AppliedManualMergeAuditEntry>> ListAppliedManualMergesAsync(Guid workspaceId, Guid sourceEnvironmentId, Guid targetEnvironmentId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AppliedManualMergeAuditEntry>>([]);
    }

    private sealed class FakePromotionBaselineService : IPromotionBaselineService
    {
        public Task<PromotionComparisonBaselineDto?> GetAsync(string sourceEnvironmentKey, string targetEnvironmentKey, CancellationToken cancellationToken) => Task.FromResult<PromotionComparisonBaselineDto?>(null);
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
