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
    IHttpClientFactory httpClientFactory) : IWorkflowApiSyncService
{
    public async Task<WorkflowApiSyncResult> SyncAsync(string environmentKey, CancellationToken cancellationToken)
        => await SyncSelectedAsync(environmentKey, [], cancellationToken);

    public async Task<WorkflowApiReconciliationPreview> PreviewAsync(string environmentKey, CancellationToken cancellationToken)
    {
        var context = await environments.GetByKeyAsync(environmentKey, cancellationToken);
        var summaries = await GetSummariesAsync(context.Environment.Key, cancellationToken);
        var local = ReadLocalWorkflows(context.Workspace.RepoPath, context.Environment.GitBranch);
        var remoteIds = summaries.Select(item => item["id"]?.GetValue<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var items = summaries.Select(item =>
        {
            var id = item["id"]?.GetValue<string>(); var name = item["name"]?.GetValue<string>() ?? id ?? "Unnamed workflow";
            (string Path, string Name, string Content)? existing = id is not null && local.TryGetValue(id, out var value) ? value : null;
            var status = existing is null ? "new-remote" : RemoteLooksChanged(item, existing.Value.Content) ? "changed-remote" : "in-sync";
            return new WorkflowApiReconciliationItem(id, name, existing?.Path, status, status != "in-sync");
        }).ToList();
        items.AddRange(local.Where(pair => !remoteIds.Contains(pair.Key)).Select(pair => new WorkflowApiReconciliationItem(pair.Key, pair.Value.Name, pair.Value.Path, "local-only", false)));
        return new WorkflowApiReconciliationPreview(context.Environment.Key, items.OrderBy(item => item.Name).ToArray(), summaries.Count, items.Count(item => item.Status == "local-only"));
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

    private Dictionary<string, (string Path, string Name, string Content)> ReadLocalWorkflows(string repoPath, string branch)
    {
        var result = new Dictionary<string, (string Path, string Name, string Content)>(StringComparer.Ordinal);
        foreach (var (path, content) in git.ReadWorkflowFilesFromBranch(repoPath, branch))
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

    private static Uri BuildUri(string baseUrl, string path, string? cursor)
    {
        var builder = new UriBuilder(new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/')));
        if (!string.IsNullOrWhiteSpace(cursor)) builder.Query = $"cursor={Uri.EscapeDataString(cursor)}";
        return builder.Uri;
    }

    private static WorkflowImportException ApiFailure(string operation, System.Net.HttpStatusCode status, string body) =>
        new($"n8n API failed while {operation} ({(int)status}): {body[..Math.Min(body.Length, 500)]}");
}
