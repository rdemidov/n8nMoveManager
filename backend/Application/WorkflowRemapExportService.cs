using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Contracts;
using Application.Models;

namespace Application;

public sealed class WorkflowRemapExportService
{
    private readonly IEnvironmentService _environmentService;
    private readonly IGitRepositoryService _gitRepositoryService;
    private readonly ICredentialMappingReader _mappingReader;
    private readonly WorkflowCredentialScanner _scanner;

    public WorkflowRemapExportService(
        IEnvironmentService environmentService,
        IGitRepositoryService gitRepositoryService,
        ICredentialMappingReader mappingReader,
        WorkflowCredentialScanner scanner)
    {
        _environmentService = environmentService;
        _gitRepositoryService = gitRepositoryService;
        _mappingReader = mappingReader;
        _scanner = scanner;
    }

    public async Task<ExportValidationResult> ValidateAsync(string sourceKey, string targetKey, CancellationToken cancellationToken)
    {
        var source = await _environmentService.GetByKeyAsync(sourceKey, cancellationToken);
        var target = await _environmentService.GetByKeyAsync(targetKey, cancellationToken);
        var files = _gitRepositoryService.ReadWorkflowFilesFromBranch(source.Workspace.RepoPath, source.Environment.GitBranch);
        var mappings = await _mappingReader.GetMappingsAsync(source.Environment.Id, target.Environment.Id, cancellationToken);
        var issues = new List<ExportValidationIssue>();

        foreach (var reference in EnumerateSourceReferences(files))
        {
            var mapping = mappings.FirstOrDefault(item => Matches(item.Source, reference));
            if (mapping is null)
            {
                issues.Add(new ExportValidationIssue(
                    "blocking",
                    $"Source credential '{reference.CredentialName ?? reference.CredentialId}' ({reference.CredentialType}) in workflow '{reference.WorkflowName}' is unmapped for target '{target.Environment.Key}'.",
                    reference.WorkflowName,
                    reference.WorkflowFilePath,
                    reference.NodeName,
                    reference.CredentialType,
                    reference.CredentialId,
                    reference.CredentialName));
                continue;
            }

            if (!string.Equals(mapping.Source.CredentialType, mapping.Target.CredentialType, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ExportValidationIssue(
                    "blocking",
                    $"Mapped credential '{mapping.Source.CredentialName ?? mapping.Source.CredentialId}' has type '{mapping.Source.CredentialType}', but target credential has type '{mapping.Target.CredentialType}'.",
                    reference.WorkflowName,
                    reference.WorkflowFilePath,
                    reference.NodeName,
                    reference.CredentialType,
                    reference.CredentialId,
                    reference.CredentialName));
            }

            if (string.IsNullOrWhiteSpace(mapping.Target.CredentialId) && string.IsNullOrWhiteSpace(mapping.Target.CredentialName))
            {
                issues.Add(new ExportValidationIssue(
                    "warning",
                    $"Target credential for logical mapping '{mapping.LogicalKey}' is missing an id and name.",
                    reference.WorkflowName,
                    reference.WorkflowFilePath,
                    reference.NodeName,
                    reference.CredentialType,
                    reference.CredentialId,
                    reference.CredentialName));
            }
        }

        return new ExportValidationResult(!issues.Any(issue => issue.Severity == "blocking"), issues);
    }

    public async Task<RemapPreviewResult> PreviewAsync(string sourceKey, string targetKey, CancellationToken cancellationToken)
    {
        var source = await _environmentService.GetByKeyAsync(sourceKey, cancellationToken);
        var target = await _environmentService.GetByKeyAsync(targetKey, cancellationToken);
        var files = _gitRepositoryService.ReadWorkflowFilesFromBranch(source.Workspace.RepoPath, source.Environment.GitBranch);
        var mappings = await _mappingReader.GetMappingsAsync(source.Environment.Id, target.Environment.Id, cancellationToken);
        var items = EnumerateSourceReferences(files).Select(reference =>
        {
            var mapping = mappings.FirstOrDefault(item => Matches(item.Source, reference));
            return mapping is null
                ? new RemapPreviewItem(reference.WorkflowName, reference.WorkflowFilePath, reference.NodeName, reference.CredentialType, reference.CredentialId, reference.CredentialName, null, null, null, "unmapped")
                : new RemapPreviewItem(reference.WorkflowName, reference.WorkflowFilePath, reference.NodeName, reference.CredentialType, reference.CredentialId, reference.CredentialName, mapping.Target.CredentialId, mapping.Target.CredentialName, mapping.LogicalKey, "will-remap");
        }).ToArray();

        return new RemapPreviewResult(items);
    }

    public async Task<(byte[] Content, IReadOnlyList<ExportValidationIssue> Warnings)> ExportAsync(string sourceKey, string targetKey, CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(sourceKey, targetKey, cancellationToken);
        if (!validation.CanExport)
        {
            throw new WorkflowImportException(string.Join(Environment.NewLine, validation.Issues.Select(issue => issue.Message)));
        }

        var source = await _environmentService.GetByKeyAsync(sourceKey, cancellationToken);
        var target = await _environmentService.GetByKeyAsync(targetKey, cancellationToken);
        var mappings = await _mappingReader.GetMappingsAsync(source.Environment.Id, target.Environment.Id, cancellationToken);
        var files = _gitRepositoryService.ReadWorkflowFilesFromBranch(source.Workspace.RepoPath, source.Environment.GitBranch);

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var root = JsonNode.Parse(content) ?? throw new WorkflowImportException($"Workflow file '{path}' could not be parsed.");
                RemapCredentials(root, mappings);
                var entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var writer = new StreamWriter(entryStream);
                await writer.WriteAsync(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        return (stream.ToArray(), validation.Issues.Where(issue => issue.Severity == "warning").ToArray());
    }

    private IEnumerable<CredentialScanItem> EnumerateSourceReferences(IReadOnlyDictionary<string, string> files)
    {
        foreach (var (path, content) in files)
        {
            using var document = JsonDocument.Parse(content);
            foreach (var workflow in EnumerateWorkflowObjects(document.RootElement))
            {
                var workflowName = GetString(workflow, "name") ?? GetString(workflow, "id") ?? Path.GetFileNameWithoutExtension(path);
                foreach (var reference in _scanner.Scan(workflow, path, GetString(workflow, "id"), workflowName))
                {
                    yield return reference;
                }
            }
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

            foreach (var child in obj.Select(property => property.Value).OfType<JsonNode>())
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

    private static bool Matches(EnvironmentCredentialSnapshot credential, CredentialScanItem reference)
    {
        return string.Equals(credential.CredentialType, reference.CredentialType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(credential.CredentialId ?? string.Empty, reference.CredentialId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(credential.CredentialName ?? string.Empty, reference.CredentialName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<JsonElement> EnumerateWorkflowObjects(JsonElement root)
    {
        return root.ValueKind switch
        {
            JsonValueKind.Object => [root],
            JsonValueKind.Array => root.EnumerateArray(),
            _ => []
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

public interface ICredentialMappingReader
{
    Task<IReadOnlyList<CredentialEnvironmentPair>> GetMappingsAsync(Guid sourceEnvironmentId, Guid targetEnvironmentId, CancellationToken cancellationToken);
}

public sealed record CredentialEnvironmentPair(
    string LogicalKey,
    EnvironmentCredentialSnapshot Source,
    EnvironmentCredentialSnapshot Target);

public sealed record EnvironmentCredentialSnapshot(
    Guid Id,
    Guid EnvironmentId,
    string CredentialType,
    string? CredentialId,
    string? CredentialName);
