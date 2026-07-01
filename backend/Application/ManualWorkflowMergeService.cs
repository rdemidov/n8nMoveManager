using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Contracts;
using Application.Models;

namespace Application;

public sealed class ManualWorkflowMergeService
{
    private static readonly ConcurrentDictionary<string, ManualMergeSessionState> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> IgnoredWorkflowFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "createdAt",
        "updatedAt",
        "versionId",
        "staticData",
        "pinData",
        "nodes",
        "connections"
    };

    private readonly IEnvironmentService _environmentService;
    private readonly IGitRepositoryService _gitRepositoryService;
    private readonly ICredentialMappingReader _mappingReader;
    private readonly IPromotionAuditService _auditService;
    private readonly IPromotionBaselineService _baselineService;
    private readonly IWorkflowMetadataService _metadataService;
    private readonly ICredentialInventoryService _credentialInventoryService;
    private readonly WorkflowCredentialScanner _credentialScanner;
    private readonly WorkflowNormalizer _normalizer;
    private readonly WorkflowSemanticDiffService _semanticDiffService;
    private readonly MergedWorkflowValidationService _validationService;

    public ManualWorkflowMergeService(
        IEnvironmentService environmentService,
        IGitRepositoryService gitRepositoryService,
        ICredentialMappingReader mappingReader,
        IPromotionAuditService auditService,
        IPromotionBaselineService baselineService,
        IWorkflowMetadataService metadataService,
        ICredentialInventoryService credentialInventoryService,
        WorkflowCredentialScanner credentialScanner,
        WorkflowNormalizer normalizer,
        WorkflowSemanticDiffService semanticDiffService,
        MergedWorkflowValidationService validationService)
    {
        _environmentService = environmentService;
        _gitRepositoryService = gitRepositoryService;
        _mappingReader = mappingReader;
        _auditService = auditService;
        _baselineService = baselineService;
        _metadataService = metadataService;
        _credentialInventoryService = credentialInventoryService;
        _credentialScanner = credentialScanner;
        _normalizer = normalizer;
        _semanticDiffService = semanticDiffService;
        _validationService = validationService;
    }

    public async Task<ManualMergeSessionDto> CreateSessionAsync(ManualMergeCreateRequest request, CancellationToken cancellationToken)
    {
        var source = await _environmentService.GetByKeyAsync(Require(request.SourceEnvironmentKey, "Source environment is required."), cancellationToken);
        var target = await _environmentService.GetByKeyAsync(Require(request.TargetEnvironmentKey, "Target environment is required."), cancellationToken);
        var workflowFilePath = NormalizeWorkflowPath(request.WorkflowFilePath);
        var sourceContent = LoadWorkflow(source, request.SourceCommitSha, workflowFilePath)
            ?? throw new WorkflowImportException($"Source workflow '{workflowFilePath}' was not found.");
        var targetContent = LoadWorkflow(target, request.TargetCommitSha, workflowFilePath)
            ?? throw new WorkflowImportException($"Target workflow '{workflowFilePath}' was not found.");
        var sourceWorkflow = ParseWorkflow(sourceContent, workflowFilePath, "source");
        var targetWorkflow = ParseWorkflow(targetContent, workflowFilePath, "target");
        var mergeInfo = _gitRepositoryService.GetMergeBaseInfo(source.Workspace.RepoPath, source.Environment.GitBranch, target.Environment.GitBranch);
        var mappings = await _mappingReader.GetMappingsAsync(source.Environment.Id, target.Environment.Id, cancellationToken);
        var semanticDiff = _semanticDiffService.CompareWorkflowContent(targetContent, sourceContent, workflowFilePath, workflowFilePath);
        var selection = BuildDefaultSelection(sourceWorkflow, targetWorkflow, semanticDiff);
        var now = DateTimeOffset.UtcNow;
        var state = new ManualMergeSessionState(
            Guid.NewGuid().ToString("N"),
            source.Workspace.Id,
            source.Environment.Id,
            source.Environment.Key,
            source.Environment.GitBranch,
            target.Environment.Id,
            target.Environment.Key,
            target.Environment.GitBranch,
            workflowFilePath,
            request.SourceCommitSha,
            request.TargetCommitSha,
            mergeInfo.BaseCommitSha,
            sourceContent,
            targetContent,
            sourceWorkflow,
            targetWorkflow,
            semanticDiff,
            selection,
            now,
            now);

        Sessions[state.Id] = state;
        return ToDto(state, mappings);
    }

    public async Task<ManualMergeSessionDto> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var state = GetState(sessionId);
        var mappings = await _mappingReader.GetMappingsAsync(state.SourceEnvironmentId, state.TargetEnvironmentId, cancellationToken);
        return ToDto(state, mappings);
    }

    public async Task<ManualMergeSessionDto> UpdateSelectionAsync(string sessionId, ManualMergeSelectionUpdateRequest request, CancellationToken cancellationToken)
    {
        var state = GetState(sessionId);
        state.Selection = NormalizeSelection(request.Selection);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        var mappings = await _mappingReader.GetMappingsAsync(state.SourceEnvironmentId, state.TargetEnvironmentId, cancellationToken);
        return ToDto(state, mappings);
    }

    public async Task<ManualMergeResultDto> PreviewAsync(string sessionId, CancellationToken cancellationToken)
    {
        var state = GetState(sessionId);
        var mappings = await _mappingReader.GetMappingsAsync(state.SourceEnvironmentId, state.TargetEnvironmentId, cancellationToken);
        return BuildResult(state, mappings);
    }

    public async Task<ManualMergeDownload> DownloadAsync(string sessionId, CancellationToken cancellationToken)
    {
        var state = GetState(sessionId);
        var mappings = await _mappingReader.GetMappingsAsync(state.SourceEnvironmentId, state.TargetEnvironmentId, cancellationToken);
        var result = BuildResult(state, mappings);
        if (result.BlockingErrors.Count > 0)
        {
            throw new WorkflowImportException(string.Join(Environment.NewLine, result.BlockingErrors));
        }

        var baseName = Path.GetFileNameWithoutExtension(state.WorkflowFilePath);
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var safeBaseName = new string(baseName.Select(character => invalidCharacters.Contains(character) ? '-' : character).ToArray());
        return new ManualMergeDownload($"{safeBaseName}-merged.json", result.ResultWorkflowJson);
    }

    public async Task<ManualMergeApplyResult> ApplyAsync(string sessionId, ManualMergeApplyRequest request, CancellationToken cancellationToken)
    {
        if (!request.Confirmation)
        {
            throw new WorkflowImportException("Manual merge confirmation is required.");
        }

        var state = GetState(sessionId);
        var mappings = await _mappingReader.GetMappingsAsync(state.SourceEnvironmentId, state.TargetEnvironmentId, cancellationToken);
        var result = BuildResult(state, mappings);
        if (result.BlockingErrors.Count > 0)
        {
            await _auditService.RecordAsync(new PromotionAuditCreate(
                state.WorkspaceId,
                state.SourceEnvironmentId,
                state.SourceEnvironmentKey,
                state.TargetEnvironmentId,
                state.TargetEnvironmentKey,
                "manual-merge-blocked",
                null,
                JsonSerializer.Serialize(new { state.WorkflowFilePath, result.BlockingErrors, result.Warnings }),
                null), cancellationToken);
            throw new WorkflowImportException(string.Join(Environment.NewLine, result.BlockingErrors));
        }

        var target = await _environmentService.GetByKeyAsync(state.TargetEnvironmentKey, cancellationToken);
        _gitRepositoryService.EnsureBranch(target.Workspace.RepoPath, target.Environment.GitBranch);
        var currentTargetCommit = _gitRepositoryService.GetRecentCommits(target.Workspace.RepoPath, target.Environment.GitBranch, 1).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(state.TargetCommitSha)
            && !string.Equals(state.TargetCommitSha, currentTargetCommit?.Sha, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowImportException("The target branch changed after this manual merge session was created. Reopen the manual merge from the current Promotion plan before applying.");
        }

        var currentTargetContent = _gitRepositoryService.ReadWorkflowFilesFromBranch(target.Workspace.RepoPath, target.Environment.GitBranch)
            .GetValueOrDefault(state.WorkflowFilePath);
        if (!string.IsNullOrWhiteSpace(currentTargetContent)
            && string.Equals(NormalizeWorkflowContent(currentTargetContent, state.WorkflowFilePath), result.ResultWorkflowJson, StringComparison.Ordinal))
        {
            return new ManualMergeApplyResult(
                false,
                null,
                null,
                state.WorkflowFilePath,
                result.Warnings,
                "No commit was created because the generated manual merge result already matches the current target workflow.");
        }

        var targetPath = Path.Combine(target.Workspace.RepoPath, state.WorkflowFilePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, result.ResultWorkflowJson, cancellationToken);
        await UpsertTargetMetadataAsync(target.Workspace.Id, target.Environment.Id, target.Environment.Key, state.WorkflowFilePath, result.ResultWorkflowJson, cancellationToken);

        var workflowName = ReadString(ParseWorkflow(result.ResultWorkflowJson, state.WorkflowFilePath, "result"), "name") ?? Path.GetFileNameWithoutExtension(state.WorkflowFilePath);
        var commit = await _gitRepositoryService.CommitChangesAsync(
            target.Workspace.RepoPath,
            target.Environment.Key,
            [state.WorkflowFilePath],
            $"Manual merge {workflowName} from {state.SourceEnvironmentKey} to {state.TargetEnvironmentKey}: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}",
            cancellationToken);

        await _auditService.RecordAsync(new PromotionAuditCreate(
            target.Workspace.Id,
            state.SourceEnvironmentId,
            state.SourceEnvironmentKey,
            target.Environment.Id,
            target.Environment.Key,
            "manual-merge-applied",
            commit.CommitSha,
            JsonSerializer.Serialize(new { state.WorkflowFilePath, result.Warnings, sessionId }),
            DateTimeOffset.UtcNow), cancellationToken);

        if (!string.IsNullOrWhiteSpace(commit.CommitSha))
        {
            await _baselineService.SetAsync(new PromotionComparisonBaselineRequest(
                state.SourceEnvironmentKey,
                state.TargetEnvironmentKey,
                commit.CommitSha,
                $"Manual merge {commit.CommitSha[..Math.Min(8, commit.CommitSha.Length)]}"), cancellationToken);
        }

        return new ManualMergeApplyResult(
            commit.CommitCreated,
            commit.CommitSha,
            commit.CommitMessage,
            state.WorkflowFilePath,
            result.Warnings,
            commit.CommitCreated ? "Manual merge committed to the target branch." : "No commit was created because the target workflow did not change.");
    }

    private ManualMergeResultDto BuildResult(ManualMergeSessionState state, IReadOnlyList<CredentialEnvironmentPair> mappings)
    {
        var selectedSourceCredentials = new List<CredentialScanItem>();
        var resultWorkflow = BuildWorkflow(state, mappings, selectedSourceCredentials);
        var raw = resultWorkflow.ToJsonString();
        using var document = JsonDocument.Parse(raw);
        var normalized = _normalizer.Normalize(document.RootElement);
        var validation = _validationService.Validate(
            state.WorkflowFilePath,
            normalized,
            state.SourceEnvironmentKey,
            state.TargetEnvironmentKey,
            mappings,
            selectedSourceCredentials,
            state.SourceWorkflow,
            state.TargetWorkflow);

        return new ManualMergeResultDto(
            normalized,
            validation.Status,
            validation.Warnings,
            validation.BlockingErrors,
            validation.InfoMessages,
            _semanticDiffService.CompareWorkflowContent(state.TargetContent, normalized, state.WorkflowFilePath, state.WorkflowFilePath),
            _semanticDiffService.CompareWorkflowContent(state.SourceContent, normalized, state.WorkflowFilePath, state.WorkflowFilePath));
    }

    private JsonObject BuildWorkflow(ManualMergeSessionState state, IReadOnlyList<CredentialEnvironmentPair> mappings, List<CredentialScanItem> selectedSourceCredentials)
    {
        var result = (state.TargetWorkflow.DeepClone() as JsonObject)
            ?? throw new WorkflowImportException("Target workflow could not be cloned.");

        foreach (var field in IgnoredWorkflowFields.Where(field => field is not "nodes" and not "connections"))
        {
            result.Remove(field);
        }

        var sourceSettings = state.SourceWorkflow;
        var targetSettings = state.TargetWorkflow;
        foreach (var selection in state.Selection.WorkflowSettingsSelections)
        {
            var sourceValue = sourceSettings[selection.PropertyName];
            var targetValue = targetSettings[selection.PropertyName];
            var value = string.Equals(selection.SelectedSide, "source", StringComparison.OrdinalIgnoreCase) ? sourceValue : targetValue;
            if (value is null)
            {
                result.Remove(selection.PropertyName);
            }
            else
            {
                result[selection.PropertyName] = value.DeepClone();
            }
        }

        var resultNodes = new JsonArray();
        foreach (var selection in state.Selection.NodeSelections)
        {
            if (selection.Resolution == "exclude")
            {
                continue;
            }

            var sourceNode = FindWorkflowNode(state.SourceWorkflow, selection.SourceNodeId, selection.NodeName, selection.NodeType);
            var targetNode = FindWorkflowNode(state.TargetWorkflow, selection.TargetNodeId, selection.NodeName, selection.NodeType);
            JsonObject? node = selection.Resolution switch
            {
                "use-source" => CloneAndTrackSourceNode(state, sourceNode, mappings, selectedSourceCredentials),
                "parameter-level" => MergeParameterLevelNode(state, selection.NodeMatchKey, sourceNode, targetNode, mappings, selectedSourceCredentials),
                _ => targetNode?.DeepClone() as JsonObject
            };

            if (node is not null)
            {
                resultNodes.Add(node);
            }
        }

        result["nodes"] = resultNodes;
        result["connections"] = string.Equals(state.Selection.ConnectionSelection, "source", StringComparison.OrdinalIgnoreCase)
            ? state.SourceWorkflow["connections"]?.DeepClone()
            : state.TargetWorkflow["connections"]?.DeepClone();
        return result;
    }

    private JsonObject? MergeParameterLevelNode(
        ManualMergeSessionState state,
        string nodeMatchKey,
        JsonObject? sourceNode,
        JsonObject? targetNode,
        IReadOnlyList<CredentialEnvironmentPair> mappings,
        List<CredentialScanItem> selectedSourceCredentials)
    {
        if (targetNode is null)
        {
            return CloneAndTrackSourceNode(state, sourceNode, mappings, selectedSourceCredentials);
        }

        var resultNode = (targetNode.DeepClone() as JsonObject)!;
        if (sourceNode is null)
        {
            return resultNode;
        }

        foreach (var parameter in state.Selection.ParameterSelections.Where(item => item.NodeMatchKey == nodeMatchKey))
        {
            if (!string.Equals(parameter.SelectedSide, "source", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadParameter(sourceNode, parameter.ParameterPath, out var sourceValue))
            {
                SetParameter(resultNode, parameter.ParameterPath, sourceValue?.DeepClone());
            }
            else
            {
                SetParameter(resultNode, parameter.ParameterPath, null);
            }
        }

        return resultNode;
    }

    private JsonObject? CloneAndTrackSourceNode(
        ManualMergeSessionState state,
        JsonObject? sourceNode,
        IReadOnlyList<CredentialEnvironmentPair> mappings,
        List<CredentialScanItem> selectedSourceCredentials)
    {
        if (sourceNode is null)
        {
            return null;
        }

        var clone = (sourceNode.DeepClone() as JsonObject)!;
        var scanWorkflow = new JsonObject
        {
            ["id"] = state.SourceWorkflow["id"]?.DeepClone(),
            ["name"] = state.SourceWorkflow["name"]?.DeepClone(),
            ["nodes"] = new JsonArray(sourceNode.DeepClone())
        };
        using var document = JsonDocument.Parse(scanWorkflow.ToJsonString());
        var workflowName = ReadString(state.SourceWorkflow, "name") ?? Path.GetFileNameWithoutExtension(state.WorkflowFilePath);
        selectedSourceCredentials.AddRange(_credentialScanner.Scan(document.RootElement, state.WorkflowFilePath, ReadString(state.SourceWorkflow, "id"), workflowName));
        if (!string.Equals(state.SourceEnvironmentKey, state.TargetEnvironmentKey, StringComparison.OrdinalIgnoreCase))
        {
            RemapCredentials(clone, mappings);
        }

        return clone;
    }

    private ManualMergeSelectionDto BuildDefaultSelection(JsonObject sourceWorkflow, JsonObject targetWorkflow, WorkflowSemanticDiffDto semanticDiff)
    {
        var nodeSelections = semanticDiff.NodeChanges.Select(node =>
        {
            var sourceNode = FindWorkflowNode(sourceWorkflow, node.NodeId, node.NodeName, node.NodeType);
            var targetNode = FindWorkflowNode(targetWorkflow, node.NodeId, node.NodeName, node.NodeType);
            var sourceId = ReadString(sourceNode, "id");
            var targetId = ReadString(targetNode, "id");
            var resolution = node.ChangeType switch
            {
                "added" => "use-source",
                "modified" => "parameter-level",
                _ => "use-target"
            };
            return new NodeMergeSelectionDto(PairMatchKey(sourceId, targetId, node.NodeName, node.NodeType), sourceId, targetId, node.NodeName, node.NodeType, node.ChangeType, resolution);
        })
        .OrderBy(node => NodeChangeOrder(node.ChangeType))
        .ThenBy(node => node.NodeName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(node => node.NodeType, StringComparer.OrdinalIgnoreCase)
        .ToArray();

        var parameters = semanticDiff.NodeChanges
            .Where(node => node.ChangeType == "modified")
            .SelectMany(node =>
            {
                var key = nodeSelections.First(selection =>
                    (!string.IsNullOrWhiteSpace(node.NodeId)
                     && (string.Equals(selection.SourceNodeId, node.NodeId, StringComparison.Ordinal)
                         || string.Equals(selection.TargetNodeId, node.NodeId, StringComparison.Ordinal)))
                    || (string.Equals(selection.NodeName, node.NodeName, StringComparison.Ordinal)
                        && string.Equals(selection.NodeType, node.NodeType, StringComparison.Ordinal))).NodeMatchKey;
                return node.ParameterChanges.Select(parameter => new ParameterMergeSelectionDto(
                    key,
                    parameter.Path,
                    parameter.NewValuePreview,
                    parameter.OldValuePreview,
                    "target"));
            })
            .ToArray();

        var settingNames = semanticDiff.WorkflowSettingsChanges
            .Select(change => change.Path.Split(new[] { '.', '[' }, StringSplitOptions.RemoveEmptyEntries)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new WorkflowSettingMergeSelectionDto(
                name,
                Preview(sourceWorkflow[name]),
                Preview(targetWorkflow[name]),
                "target"))
            .ToArray();

        return new ManualMergeSelectionDto(settingNames, nodeSelections, parameters, "target", "target-mappings");
    }

    private ManualMergeSessionDto ToDto(ManualMergeSessionState state, IReadOnlyList<CredentialEnvironmentPair> mappings) =>
        new(
            state.Id,
            state.SourceEnvironmentKey,
            state.TargetEnvironmentKey,
            state.WorkflowFilePath,
            state.SourceCommitSha,
            state.TargetCommitSha,
            state.BaseCommitSha,
            state.CreatedAt,
            state.UpdatedAt,
            SummarizeWorkflow(state.WorkflowFilePath, state.SourceWorkflow, mappings, isSource: true, sameEnvironment: string.Equals(state.SourceEnvironmentKey, state.TargetEnvironmentKey, StringComparison.OrdinalIgnoreCase)),
            SummarizeWorkflow(state.WorkflowFilePath, state.TargetWorkflow, mappings, isSource: false, sameEnvironment: string.Equals(state.SourceEnvironmentKey, state.TargetEnvironmentKey, StringComparison.OrdinalIgnoreCase)),
            state.SemanticDiff,
            state.Selection);

    private ManualMergeWorkflowSummaryDto SummarizeWorkflow(string path, JsonObject workflow, IReadOnlyList<CredentialEnvironmentPair> mappings, bool isSource, bool sameEnvironment)
    {
        using var document = JsonDocument.Parse(workflow.ToJsonString());
        var name = ReadString(workflow, "name") ?? Path.GetFileNameWithoutExtension(path);
        var credentials = _credentialScanner.Scan(document.RootElement, path, ReadString(workflow, "id"), name)
            .Select(reference =>
            {
                var mapping = mappings.FirstOrDefault(item => Matches(item.Source, reference));
                return new ManualMergeCredentialReferenceDto(
                    reference.NodeName,
                    reference.CredentialType,
                    reference.CredentialType,
                    reference.CredentialId,
                    reference.CredentialName,
                    !isSource || sameEnvironment || mapping is not null,
                    mapping?.Target.CredentialId,
                    mapping?.Target.CredentialName);
            })
            .ToArray();

        return new ManualMergeWorkflowSummaryDto(
            ReadString(workflow, "id"),
            name,
            TryGetBool(workflow, "active", out var active) && active,
            workflow["nodes"] is JsonArray nodes ? nodes.Count : 0,
            credentials);
    }

    private string? LoadWorkflow(EnvironmentContext context, string? commitSha, string workflowFilePath)
    {
        if (!string.IsNullOrWhiteSpace(commitSha))
        {
            return _gitRepositoryService.ReadWorkflowFileFromCommit(context.Workspace.RepoPath, commitSha.Trim(), workflowFilePath);
        }

        return _gitRepositoryService.ReadWorkflowFilesFromBranch(context.Workspace.RepoPath, context.Environment.GitBranch)
            .GetValueOrDefault(workflowFilePath);
    }

    private async Task UpsertTargetMetadataAsync(Guid workspaceId, Guid environmentId, string environmentKey, string filePath, string content, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(content);
        var workflow = document.RootElement;
        var name = ReadString(workflow, "name") ?? ReadString(workflow, "id") ?? Path.GetFileNameWithoutExtension(filePath);
        var nodesCount = workflow.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array ? nodes.GetArrayLength() : 0;
        var update = new WorkflowMetadataUpdate(
            workspaceId,
            environmentId,
            environmentKey,
            ReadString(workflow, "id"),
            name,
            workflow.TryGetProperty("active", out var active) && active.ValueKind is JsonValueKind.True or JsonValueKind.False && active.GetBoolean(),
            nodesCount,
            null,
            null,
            filePath,
            DateTimeOffset.UtcNow);
        await _metadataService.UpsertAsync(update, cancellationToken);
        await _credentialInventoryService.ReplaceWorkflowReferencesAsync(
            workspaceId,
            environmentId,
            environmentKey,
            filePath,
            _credentialScanner.Scan(workflow, filePath, update.ExternalId, update.Name),
            cancellationToken);
    }

    private string NormalizeWorkflowContent(string content, string filePath)
    {
        using var document = JsonDocument.Parse(content);
        return _normalizer.Normalize(document.RootElement);
    }

    private static ManualMergeSelectionDto NormalizeSelection(ManualMergeSelectionDto selection) =>
        selection with
        {
            WorkflowSettingsSelections = selection.WorkflowSettingsSelections
                .Select(item => item with { SelectedSide = NormalizeSide(item.SelectedSide) })
                .ToArray(),
            NodeSelections = selection.NodeSelections
                .Select(item => item with { Resolution = NormalizeResolution(item.Resolution) })
                .ToArray(),
            ParameterSelections = selection.ParameterSelections
                .Select(item => item with { SelectedSide = NormalizeSide(item.SelectedSide) })
                .ToArray(),
            ConnectionSelection = NormalizeSide(selection.ConnectionSelection),
            CredentialMappingMode = "target-mappings"
        };

    private static JsonObject? FindWorkflowNode(JsonObject workflow, string? nodeId, string nodeName, string nodeType)
    {
        var nodes = (workflow["nodes"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        return !string.IsNullOrWhiteSpace(nodeId)
            ? nodes.FirstOrDefault(node => string.Equals(ReadString(node, "id"), nodeId, StringComparison.Ordinal))
              ?? nodes.FirstOrDefault(node => SameNodeNameAndType(node, nodeName, nodeType))
            : nodes.FirstOrDefault(node => SameNodeNameAndType(node, nodeName, nodeType));
    }

    private static bool SameNodeNameAndType(JsonObject node, string nodeName, string nodeType) =>
        string.Equals(ReadString(node, "name"), nodeName, StringComparison.Ordinal)
        && string.Equals(ReadString(node, "type"), nodeType, StringComparison.Ordinal);

    private static string PairMatchKey(string? sourceNodeId, string? targetNodeId, string nodeName, string nodeType) =>
        $"pair:{sourceNodeId ?? "-"}|{targetNodeId ?? "-"}|{nodeName}|{nodeType}";

    private static bool TryReadParameter(JsonObject node, string path, out JsonNode? value)
    {
        value = null;
        var current = node["parameters"];
        if (current is null)
        {
            return false;
        }

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current))
            {
                return false;
            }
        }

        value = current;
        return true;
    }

    private static void SetParameter(JsonObject node, string path, JsonNode? value)
    {
        if (node["parameters"] is not JsonObject parameters)
        {
            parameters = [];
            node["parameters"] = parameters;
        }

        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = parameters;
        for (var i = 0; i < parts.Length; i++)
        {
            if (i == parts.Length - 1)
            {
                if (value is null)
                {
                    current.Remove(parts[i]);
                }
                else
                {
                    current[parts[i]] = value;
                }
                return;
            }

            if (current[parts[i]] is not JsonObject child)
            {
                child = [];
                current[parts[i]] = child;
            }

            current = child;
        }
    }

    private static void RemapCredentials(JsonNode node, IReadOnlyList<CredentialEnvironmentPair> mappings)
    {
        if (node is JsonObject obj)
        {
            if (obj["credentials"] is JsonObject credentials)
            {
                foreach (var property in credentials.ToArray())
                {
                    if (property.Value is JsonObject credential)
                    {
                        RemapCredentialObject(property.Key, credential, mappings);
                    }
                    else if (property.Value is JsonArray array)
                    {
                        foreach (var item in array.OfType<JsonObject>())
                        {
                            RemapCredentialObject(property.Key, item, mappings);
                        }
                    }
                }
            }

            foreach (var child in obj.Select(item => item.Value).OfType<JsonNode>())
            {
                RemapCredentials(child, mappings);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>())
            {
                RemapCredentials(child, mappings);
            }
        }
    }

    private static void RemapCredentialObject(string fallbackType, JsonObject credential, IReadOnlyList<CredentialEnvironmentPair> mappings)
    {
        var sourceType = credential["type"]?.GetValue<string>() ?? fallbackType;
        var sourceId = credential["id"]?.GetValue<string>();
        var sourceName = credential["name"]?.GetValue<string>();
        var mapping = mappings.FirstOrDefault(item =>
            string.Equals(item.Source.CredentialType, sourceType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Source.CredentialId ?? string.Empty, sourceId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Source.CredentialName ?? string.Empty, sourceName ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        if (mapping is null)
        {
            return;
        }

        credential["id"] = mapping.Target.CredentialId;
        credential["name"] = mapping.Target.CredentialName;
        credential["type"] = mapping.Target.CredentialType;
    }

    private static ManualMergeSessionState GetState(string sessionId) =>
        Sessions.TryGetValue(sessionId, out var state)
            ? state
            : throw new WorkflowImportException($"Manual merge session '{sessionId}' was not found.");

    private static JsonObject ParseWorkflow(string content, string path, string side) =>
        JsonNode.Parse(content) as JsonObject
        ?? throw new WorkflowImportException($"{side} workflow '{path}' must be a JSON object.");

    private static string NormalizeWorkflowPath(string filePath)
    {
        var path = Require(filePath, "Workflow file path is required.").Replace('\\', '/');
        if (!path.StartsWith("workflows/", StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || path.Split('/').Any(segment => segment is "." or ".." || string.IsNullOrWhiteSpace(segment)))
        {
            throw new WorkflowImportException("Manual merge only supports workflow JSON paths under workflows/.");
        }

        return path;
    }

    private static string Require(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new WorkflowImportException(message) : value.Trim();

    private static string NormalizeSide(string side) =>
        string.Equals(side, "source", StringComparison.OrdinalIgnoreCase) ? "source" : "target";

    private static string NormalizeResolution(string resolution) =>
        resolution?.Trim().ToLowerInvariant() switch
        {
            "use-source" => "use-source",
            "exclude" => "exclude",
            "parameter-level" => "parameter-level",
            _ => "use-target"
        };

    private static int NodeChangeOrder(string changeType) => changeType switch
    {
        "modified" => 0,
        "added" => 1,
        "removed" => 2,
        _ => 3
    };

    private static string? ReadString(JsonObject? obj, string propertyName) =>
        obj?[propertyName]?.GetValue<string>();

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGetBool(JsonObject obj, string propertyName, out bool value)
    {
        if (obj[propertyName] is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out value))
        {
            return true;
        }

        value = false;
        return false;
    }

    private static bool JsonEquals(JsonNode? left, JsonNode? right) =>
        string.Equals(left?.ToJsonString(), right?.ToJsonString(), StringComparison.Ordinal);

    private static string? Preview(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var value = node.ToJsonString(WriteOptions);
        return value.Length <= 200 ? value : value[..200] + "...";
    }

    private static bool Matches(EnvironmentCredentialSnapshot credential, CredentialScanItem reference) =>
        string.Equals(credential.CredentialType, reference.CredentialType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(credential.CredentialId ?? string.Empty, reference.CredentialId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && string.Equals(credential.CredentialName ?? string.Empty, reference.CredentialName ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private sealed class ManualMergeSessionState
    {
        public ManualMergeSessionState(
            string id,
            Guid workspaceId,
            Guid sourceEnvironmentId,
            string sourceEnvironmentKey,
            string sourceGitBranch,
            Guid targetEnvironmentId,
            string targetEnvironmentKey,
            string targetGitBranch,
            string workflowFilePath,
            string? sourceCommitSha,
            string? targetCommitSha,
            string? baseCommitSha,
            string sourceContent,
            string targetContent,
            JsonObject sourceWorkflow,
            JsonObject targetWorkflow,
            WorkflowSemanticDiffDto semanticDiff,
            ManualMergeSelectionDto selection,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt)
        {
            Id = id;
            WorkspaceId = workspaceId;
            SourceEnvironmentId = sourceEnvironmentId;
            SourceEnvironmentKey = sourceEnvironmentKey;
            SourceGitBranch = sourceGitBranch;
            TargetEnvironmentId = targetEnvironmentId;
            TargetEnvironmentKey = targetEnvironmentKey;
            TargetGitBranch = targetGitBranch;
            WorkflowFilePath = workflowFilePath;
            SourceCommitSha = sourceCommitSha;
            TargetCommitSha = targetCommitSha;
            BaseCommitSha = baseCommitSha;
            SourceContent = sourceContent;
            TargetContent = targetContent;
            SourceWorkflow = sourceWorkflow;
            TargetWorkflow = targetWorkflow;
            SemanticDiff = semanticDiff;
            Selection = selection;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
        }

        public string Id { get; }
        public Guid WorkspaceId { get; }
        public Guid SourceEnvironmentId { get; }
        public string SourceEnvironmentKey { get; }
        public string SourceGitBranch { get; }
        public Guid TargetEnvironmentId { get; }
        public string TargetEnvironmentKey { get; }
        public string TargetGitBranch { get; }
        public string WorkflowFilePath { get; }
        public string? SourceCommitSha { get; }
        public string? TargetCommitSha { get; }
        public string? BaseCommitSha { get; }
        public string SourceContent { get; }
        public string TargetContent { get; }
        public JsonObject SourceWorkflow { get; }
        public JsonObject TargetWorkflow { get; }
        public WorkflowSemanticDiffDto SemanticDiff { get; }
        public ManualMergeSelectionDto Selection { get; set; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
