using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application;
using Application.Contracts;
using Application.Models;

namespace Infrastructure;

/// <summary>Imports complete workflow definitions through n8n's public API into the environment Git branch.</summary>
public sealed class WorkflowApiSyncService(
    IEnvironmentService environments,
    IGitRepositoryService git,
    IEnvironmentN8nApiConfigStore configStore,
    IWorkflowImportService importService,
    WorkflowNormalizer normalizer,
    WorkflowSemanticDiffService semanticDiffService,
    IHttpClientFactory httpClientFactory) : IWorkflowApiSyncService
{
    public async Task<WorkflowApiSyncResult> SyncAsync(string environmentKey, CancellationToken cancellationToken)
        => await SyncSelectedAsync(environmentKey, [], cancellationToken);

    public async Task<WorkflowApiReconciliationPreview> PreviewAsync(string environmentKey, CancellationToken cancellationToken)
    {
        var context = await environments.GetByKeyAsync(environmentKey, cancellationToken);
        var summaries = await GetSummariesAsync(context.Environment.Key, cancellationToken);
        var currentFiles = git.ReadWorkflowFilesFromBranch(context.Workspace.RepoPath, context.Environment.GitBranch);
        var local = ReadLocalWorkflows(currentFiles);
        var remoteIds = summaries.Select(item => item["id"]?.GetValue<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var remoteDetails = await GetWorkflowDetailsAsync(context.Environment.Key, remoteIds, cancellationToken);
        var remoteFiles = BuildNormalizedWorkflowFiles(remoteDetails);
        var remoteFilesById = remoteFiles.Values.ToDictionary(file => file.WorkflowId, StringComparer.Ordinal);
        var previewOldFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previewNewFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in remoteFiles)
        {
            if (!currentFiles.TryGetValue(file.Key, out var oldContent)
                && local.TryGetValue(file.Value.WorkflowId, out var existing))
            {
                oldContent = existing.Content;
            }

            previewOldFiles[file.Key] = oldContent ?? string.Empty;
            previewNewFiles[file.Key] = file.Value.Content;
        }

        var exactStatuses = remoteFiles.ToDictionary(
            file => file.Value.WorkflowId,
            file => currentFiles.TryGetValue(file.Key, out var oldContent)
                ? string.Equals(oldContent, file.Value.Content, StringComparison.Ordinal) ? "in-sync" : "changed-remote"
                : local.ContainsKey(file.Value.WorkflowId) ? "changed-remote"
                : "new-remote",
            StringComparer.Ordinal);
        var changePreview = semanticDiffService.CompareWorkflowFiles(
            previewOldFiles,
            previewNewFiles,
            context.Environment.Key,
            "n8n API");

        var items = summaries.Select(item =>
        {
            var id = item["id"]?.GetValue<string>(); var name = item["name"]?.GetValue<string>() ?? id ?? "Unnamed workflow";
            var filePath = id is not null && remoteFilesById.TryGetValue(id, out var remoteFile)
                ? remoteFile.FilePath
                : id is not null && local.TryGetValue(id, out var value)
                    ? value.Path
                    : null;
            var status = id is not null && exactStatuses.TryGetValue(id, out var exactStatus)
                ? exactStatus
                : id is not null && local.TryGetValue(id, out var existing) && RemoteLooksChanged(item, existing.Content)
                    ? "changed-remote"
                    : local.ContainsKey(id ?? string.Empty) ? "in-sync" : "new-remote";
            return new WorkflowApiReconciliationItem(id, name, filePath, status, status != "in-sync");
        }).ToList();
        items.AddRange(local.Where(pair => !remoteIds.Contains(pair.Key)).Select(pair => new WorkflowApiReconciliationItem(pair.Key, pair.Value.Name, pair.Value.Path, "local-only", false)));
        return new WorkflowApiReconciliationPreview(context.Environment.Key, items.OrderBy(item => item.Name).ToArray(), summaries.Count, items.Count(item => item.Status == "local-only"), changePreview);
    }

    public async Task<WorkflowApiSyncResult> SyncSelectedAsync(string environmentKey, IReadOnlyCollection<string> workflowIds, CancellationToken cancellationToken)
    {
        var context = await environments.GetByKeyAsync(environmentKey, cancellationToken);
        var summaries = await GetSummariesAsync(context.Environment.Key, cancellationToken);
        var requested = workflowIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        if (requested.Count > 0) summaries = summaries.Where(item => requested.Contains(item["id"]?.GetValue<string>() ?? string.Empty)).ToList();
        if (requested.Count > 0 && summaries.Count != requested.Count) throw new WorkflowImportException("One or more selected workflows no longer exist in n8n. Refresh the preview.");
        var config = await configStore.GetAsync(context.Environment.Key, cancellationToken);
        var apiKey = await configStore.GetApiKeyAsync(context.Environment.Key, cancellationToken) ?? throw new WorkflowImportException("An n8n API key is required to sync workflows.");

        var sources = new List<WorkflowUploadSource>();
        var client = httpClientFactory.CreateClient("n8n");
        foreach (var summary in summaries)
        {
            var id = summary["id"]?.GetValue<string>() ?? throw new WorkflowImportException("n8n returned a workflow without an id.");
            var uri = BuildUri(config.BaseUrl, $"{config.WorkflowApiPath.TrimEnd('/')}/{Uri.EscapeDataString(id)}", null);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.Add("X-N8N-API-KEY", apiKey);
            using var response = await client.SendAsync(request, cancellationToken); var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw ApiFailure($"fetching workflow '{id}'", response.StatusCode, content);
            sources.Add(new WorkflowUploadSource($"n8n-api-{id}.json", content));
        }

        if (sources.Count == 0) return new WorkflowApiSyncResult(context.Environment.Key, 0, 0, 0, null, true, 0, ["No selected workflows were returned by n8n."]);
        var import = await importService.ImportAsync(context.Environment.Key, sources, $"Sync workflows from n8n API: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}", cancellationToken);
        var warnings = import.CommitSha is null ? new[] { "No workflow file changes were detected; no Git commit was created." } : Array.Empty<string>();
        return new WorkflowApiSyncResult(context.Environment.Key, summaries.Count, import.ImportedWorkflowsCount, import.ChangedFilesCount, import.CommitSha, import.CommitSha is null, import.CredentialReferencesScanned, warnings);
    }

    private async Task<List<JsonObject>> GetSummariesAsync(string environmentKey, CancellationToken cancellationToken)
    {
        var config = await configStore.GetAsync(environmentKey, cancellationToken);
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.BaseUrl)) throw new WorkflowImportException("Enable the n8n API connection and provide a base URL first.");
        var apiKey = await configStore.GetApiKeyAsync(environmentKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new WorkflowImportException("An n8n API key is required to sync workflows.");
        var client = httpClientFactory.CreateClient("n8n"); var summaries = new List<JsonObject>();
        string? cursor = null;
        do
        {
            var uri = BuildUri(config.BaseUrl, config.WorkflowApiPath, cursor);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("X-N8N-API-KEY", apiKey);
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw ApiFailure("listing workflows", response.StatusCode, content);
            var page = JsonNode.Parse(content)?.AsObject() ?? throw new WorkflowImportException("n8n returned an invalid workflow-list response.");
            var data = page["data"]?.AsArray() ?? throw new WorkflowImportException("n8n workflow-list response did not include a data array.");
            summaries.AddRange(data.OfType<JsonObject>());
            cursor = page["nextCursor"]?.GetValue<string>();
        } while (!string.IsNullOrWhiteSpace(cursor));

        return summaries;
    }

    private async Task<IReadOnlyList<string>> GetWorkflowDetailsAsync(string environmentKey, IReadOnlyCollection<string> workflowIds, CancellationToken cancellationToken)
    {
        if (workflowIds.Count == 0)
        {
            return [];
        }

        var config = await configStore.GetAsync(environmentKey, cancellationToken);
        var apiKey = await configStore.GetApiKeyAsync(environmentKey, cancellationToken) ?? throw new WorkflowImportException("An n8n API key is required to preview workflow changes.");
        var client = httpClientFactory.CreateClient("n8n");
        var details = new List<string>();
        foreach (var id in workflowIds)
        {
            var uri = BuildUri(config.BaseUrl, $"{config.WorkflowApiPath.TrimEnd('/')}/{Uri.EscapeDataString(id)}", null);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("X-N8N-API-KEY", apiKey);
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw ApiFailure($"fetching workflow '{id}' for preview", response.StatusCode, content);
            }

            details.Add(content);
        }

        return details;
    }

    private Dictionary<string, (string WorkflowId, string FilePath, string Content)> BuildNormalizedWorkflowFiles(IEnumerable<string> workflowContents)
    {
        var files = new Dictionary<string, (string WorkflowId, string FilePath, string Content)>(StringComparer.OrdinalIgnoreCase);
        foreach (var content in workflowContents)
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new WorkflowImportException("n8n returned a workflow preview payload that was not a JSON object.");
            }

            var id = ReadString(document.RootElement, "id") ?? throw new WorkflowImportException("n8n returned a workflow without an id.");
            var name = ReadString(document.RootElement, "name") ?? id;
            var filePath = $"workflows/{MakeSafeFileName(name)}.json";
            files[filePath] = (id, filePath, normalizer.Normalize(document.RootElement));
        }

        return files;
    }

    private Dictionary<string, (string Path, string Name, string Content)> ReadLocalWorkflows(IReadOnlyDictionary<string, string> files)
    {
        var result = new Dictionary<string, (string Path, string Name, string Content)>(StringComparer.Ordinal);
        foreach (var (path, content) in files)
        {
            try
            {
                var node = JsonNode.Parse(content)?.AsObject();
                var id = node?["id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(id)) result[id] = (path, node?["name"]?.GetValue<string>() ?? id, content);
            }
            catch (JsonException) { }
        }
        return result;
    }
    private static bool RemoteLooksChanged(JsonObject remote, string content) => remote["updatedAt"]?.GetValue<string>() is { } updated && !content.Contains(updated, StringComparison.Ordinal);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    private static Uri BuildUri(string baseUrl, string path, string? cursor)
    {
        var builder = new UriBuilder(new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/')));
        if (!string.IsNullOrWhiteSpace(cursor)) builder.Query = $"cursor={Uri.EscapeDataString(cursor)}";
        return builder.Uri;
    }

    private static WorkflowImportException ApiFailure(string operation, System.Net.HttpStatusCode status, string body) =>
        new($"n8n API failed while {operation} ({(int)status}): {body[..Math.Min(body.Length, 500)]}");
}
