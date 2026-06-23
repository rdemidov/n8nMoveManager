using System.Text.Json;
using Application.Contracts;
using Application.Models;

namespace Application;

public sealed class WorkflowImportService : IWorkflowImportService
{
    private readonly IEnvironmentService _environmentService;
    private readonly IWorkflowMetadataService _metadataService;
    private readonly ICredentialInventoryService _credentialInventoryService;
    private readonly IGitRepositoryService _gitRepositoryService;
    private readonly WorkflowNormalizer _normalizer;
    private readonly WorkflowCredentialScanner _credentialScanner;

    public WorkflowImportService(
        IEnvironmentService environmentService,
        IWorkflowMetadataService metadataService,
        ICredentialInventoryService credentialInventoryService,
        IGitRepositoryService gitRepositoryService,
        WorkflowNormalizer normalizer,
        WorkflowCredentialScanner credentialScanner)
    {
        _environmentService = environmentService;
        _metadataService = metadataService;
        _credentialInventoryService = credentialInventoryService;
        _gitRepositoryService = gitRepositoryService;
        _normalizer = normalizer;
        _credentialScanner = credentialScanner;
    }

    public async Task<UploadResultDto> ImportAsync(string environmentKey, IReadOnlyCollection<WorkflowUploadSource> sources, string? commitMessage, CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            throw new WorkflowImportException("Upload was empty. Provide JSON in the request body or upload one or more .json files.");
        }

        var environmentContext = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        var workspace = environmentContext.Workspace;
        var environment = environmentContext.Environment;
        _gitRepositoryService.EnsureRepository(workspace.RepoPath);
        _gitRepositoryService.EnsureBranch(workspace.RepoPath, environment.GitBranch);

        var workflowsRoot = Path.Combine(workspace.RepoPath, "workflows");
        Directory.CreateDirectory(workflowsRoot);

        var imported = new List<WorkflowImportItemDto>();
        var metadataUpdates = new List<WorkflowMetadataUpdate>();
        var credentialUpdates = new List<(string FilePath, IReadOnlyCollection<CredentialScanItem> References)>();
        var credentialReferencesScanned = 0;
        var changedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        var workflowInputs = new List<(JsonDocument Document, IReadOnlyList<JsonElement> Workflows)>();
        try
        {
            foreach (var source in sources)
            {
                if (!string.IsNullOrWhiteSpace(source.FileName)
                    && !source.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new WorkflowImportException($"Unsupported file format for '{source.FileName}'. Only .json files are supported.");
                }

                var document = ParseJson(source.Content, source.FileName);
                var workflows = EnumerateWorkflowObjects(document.RootElement, source.FileName).ToArray();
                foreach (var workflow in workflows)
                {
                    _ = ReadMetadata(workflow);
                }

                workflowInputs.Add((document, workflows));
            }

            foreach (var (_, workflows) in workflowInputs)
            {
                foreach (var workflow in workflows)
                {
                    var metadata = ReadMetadata(workflow);
                    var safeFileName = MakeSafeFileName(!string.IsNullOrWhiteSpace(metadata.Name) ? metadata.Name : metadata.ExternalId);
                    var relativePath = $"workflows/{safeFileName}.json";
                    var absolutePath = Path.Combine(workspace.RepoPath, "workflows", $"{safeFileName}.json");
                    var normalized = _normalizer.Normalize(workflow);

                    await File.WriteAllTextAsync(absolutePath, normalized, cancellationToken);
                    changedRelativePaths.Add(relativePath);

                    imported.Add(new WorkflowImportItemDto(metadata.ExternalId, metadata.Name, metadata.Active, metadata.NodesCount, relativePath));
                    metadataUpdates.Add(metadata with
                    {
                        WorkspaceId = workspace.Id,
                        EnvironmentId = environment.Id,
                        EnvironmentKey = environment.Key,
                        FilePath = relativePath,
                        LastImportedAt = now
                    });
                    var references = _credentialScanner.Scan(workflow, relativePath, metadata.ExternalId, metadata.Name);
                    credentialReferencesScanned += references.Count;
                    credentialUpdates.Add((relativePath, references));
                }
            }
        }
        finally
        {
            foreach (var (document, _) in workflowInputs)
            {
                document.Dispose();
            }
        }

        if (imported.Count == 0)
        {
            throw new WorkflowImportException("No workflow objects were found in the upload.");
        }

        foreach (var update in metadataUpdates)
        {
            await _metadataService.UpsertAsync(update, cancellationToken);
        }

        foreach (var update in credentialUpdates)
        {
            await _credentialInventoryService.ReplaceWorkflowReferencesAsync(
                workspace.Id,
                environment.Id,
                environment.Key,
                update.FilePath,
                update.References,
                cancellationToken);
        }

        var commit = await _gitRepositoryService.CommitChangesAsync(workspace.RepoPath, environment.Key, changedRelativePaths, commitMessage, cancellationToken);

        return new UploadResultDto(
            imported.Count,
            commit.ChangedFilesCount,
            commit.CommitSha,
            commit.CommitMessage,
            commit.Message,
            imported,
            credentialReferencesScanned);
    }

    private static JsonDocument ParseJson(string content, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new WorkflowImportException($"'{fileName ?? "request body"}' was empty.");
        }

        try
        {
            return JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new WorkflowImportException($"Invalid JSON in '{fileName ?? "request body"}': {ex.Message}");
        }
    }

    private static IEnumerable<JsonElement> EnumerateWorkflowObjects(JsonElement root, string? fileName)
    {
        var workflows = root.ValueKind switch
        {
            JsonValueKind.Object => EnumerateObjectOrWrapper(root),
            JsonValueKind.Array => root.EnumerateArray().ToArray(),
            _ => throw new WorkflowImportException("Upload JSON must be a workflow object or an array of workflow objects.")
        };

        var workflowArray = workflows.ToArray();
        if (workflowArray.Length > 0 && workflowArray.All(IsCredentialObject))
        {
            throw new WorkflowImportException($"'{fileName ?? "upload"}' appears to be an n8n credentials export. Credential exports are not imported by n8n Move Manager. Upload workflow exports only.");
        }

        return workflowArray;
    }

    private static WorkflowMetadataUpdate ReadMetadata(JsonElement workflow)
    {
        if (workflow.ValueKind != JsonValueKind.Object)
        {
            throw new WorkflowImportException("Each item in an upload array must be a workflow object.");
        }

        var externalId = GetString(workflow, "id");
        var name = GetString(workflow, "name");
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(externalId))
        {
            throw new WorkflowImportException("Malformed n8n workflow object. A workflow must include at least 'name' or 'id'.");
        }

        if (!workflow.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            if (IsCredentialObject(workflow))
            {
                throw new WorkflowImportException($"'{name ?? externalId ?? "upload"}' appears to be an n8n credential, not a workflow. Credential exports are not imported by n8n Move Manager.");
            }

            throw new WorkflowImportException($"Malformed n8n workflow '{name ?? externalId}'. Expected a 'nodes' array.");
        }

        return new WorkflowMetadataUpdate(
            Guid.Empty,
            Guid.Empty,
            string.Empty,
            externalId,
            string.IsNullOrWhiteSpace(name) ? externalId! : name!,
            GetBool(workflow, "active"),
            nodes.GetArrayLength(),
            GetDate(workflow, "createdAt"),
            GetDate(workflow, "updatedAt"),
            string.Empty,
            DateTimeOffset.MinValue);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static IReadOnlyList<JsonElement> EnumerateObjectOrWrapper(JsonElement root)
    {
        if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            return [root];
        }

        foreach (var propertyName in new[] { "workflows", "data" })
        {
            if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray().ToArray();
            }
        }

        return [root];
    }

    private static bool IsCredentialObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("data", out var data)
        && data.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && !element.TryGetProperty("nodes", out _);

    private static bool GetBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();
    }

    private static DateTimeOffset? GetDate(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed)
                ? parsed
                : null;
    }

    private static string MakeSafeFileName(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value.Trim();
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = source
            .Select(c => invalid.Contains(c) ? '-' : c)
            .Select(c => char.IsWhiteSpace(c) ? '-' : c)
            .ToArray();

        var safe = new string(chars).Trim('-', '.');
        return string.IsNullOrWhiteSpace(safe) ? Guid.NewGuid().ToString("N") : safe;
    }
}
