using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Application;
using Application.Contracts;
using Application.Models;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public sealed class WorkflowDeploymentService(AppDbContext db, IEnvironmentService environments, IGitRepositoryService git, IEnvironmentN8nApiConfigStore configStore, IHttpClientFactory httpClientFactory, IPromotionAuditService auditService, ICredentialMappingReader credentialMappings, WorkflowCredentialScanner credentialScanner) : IWorkflowDeploymentService
{
    public async Task<WorkflowDeploymentPreview> PreviewAsync(WorkflowDeploymentPreviewRequest request, CancellationToken cancellationToken)
    {
        var source = await environments.GetByKeyAsync(request.SourceEnvironmentKey, cancellationToken);
        var target = await environments.GetByKeyAsync(request.TargetEnvironmentKey, cancellationToken);
        var errors = new List<string>();
        if (source.Environment.Id == target.Environment.Id) errors.Add("Source and target environments must be different.");
        var config = await db.EnvironmentN8nApiConfigs.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentId == target.Environment.Id, cancellationToken);
        var apiKey = await configStore.GetApiKeyAsync(target.Environment.Key, cancellationToken);
        if (config is null || !config.Enabled || string.IsNullOrWhiteSpace(apiKey)) errors.Add("The target needs an enabled n8n API connection with an API key.");
        var sourceFiles = git.ReadWorkflowFilesFromBranch(source.Workspace.RepoPath, source.Environment.GitBranch);
        var targetFiles = git.ReadWorkflowFilesFromBranch(target.Workspace.RepoPath, target.Environment.GitBranch);
        var targetCredentials = await db.EnvironmentCredentials.AsNoTracking().Where(item => item.EnvironmentId == target.Environment.Id).ToArrayAsync(cancellationToken);
        var mappings = await credentialMappings.GetMappingsAsync(source.Environment.Id, target.Environment.Id, cancellationToken);
        var requested = request.WorkflowFilePaths.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length == 0) errors.Add("Select at least one workflow to deploy.");
        var items = new List<WorkflowDeploymentItem>();
        var n8nClient = httpClientFactory.CreateClient("n8n");
        foreach (var path in requested)
        {
            if (!sourceFiles.TryGetValue(path, out var content)) { errors.Add($"Workflow '{path}' is no longer present in the source snapshot."); continue; }
            try
            {
                var sourceNode = JsonNode.Parse(content)?.AsObject() ?? throw new JsonException();
                var name = sourceNode["name"]?.GetValue<string>() ?? path;
                var targetId = targetFiles.TryGetValue(path, out var targetContent) ? JsonNode.Parse(targetContent)?["id"]?.GetValue<string>() : null;
                var warnings = new List<string>();
                if (!string.IsNullOrWhiteSpace(targetId) && !string.IsNullOrWhiteSpace(config?.BaseUrl) && !string.IsNullOrWhiteSpace(apiKey) && targetContent is not null)
                {
                    var snapshot = JsonNode.Parse(targetContent)?.AsObject();
                    var snapshotUpdatedAt = ReadTimestamp(snapshot?["updatedAt"]);
                    var liveUpdatedAt = await GetLiveUpdatedAtAsync(n8nClient, config.BaseUrl, config.WorkflowApiPath, targetId, apiKey, cancellationToken);
                    if (snapshotUpdatedAt.HasValue && liveUpdatedAt.HasValue && liveUpdatedAt > snapshotUpdatedAt)
                    {
                        var conflict = $"Live target workflow changed after its Git snapshot ({liveUpdatedAt:O}); refresh/reconcile the target before deployment.";
                        warnings.Add(conflict);
                        errors.Add($"Conflict for '{name}': {conflict}");
                    }
                }
                using var document = JsonDocument.Parse(content);
                foreach (var reference in credentialScanner.Scan(document.RootElement, path, sourceNode["id"]?.GetValue<string>(), name))
                {
                    var mapped = mappings.Any(map => string.Equals(map.Source.CredentialType, reference.CredentialType, StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(map.Source.CredentialId, reference.CredentialId, StringComparison.Ordinal) || (!string.IsNullOrWhiteSpace(reference.CredentialName) && string.Equals(map.Source.CredentialName, reference.CredentialName, StringComparison.OrdinalIgnoreCase))));
                    var present = targetCredentials.Any(credential => string.Equals(credential.CredentialType, reference.CredentialType, StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(credential.CredentialId, reference.CredentialId, StringComparison.Ordinal) || (!string.IsNullOrWhiteSpace(reference.CredentialName) && string.Equals(credential.CredentialName, reference.CredentialName, StringComparison.OrdinalIgnoreCase))));
                    if (!mapped && !present) warnings.Add($"Missing target credential: {reference.CredentialType} ({reference.CredentialName ?? reference.CredentialId ?? "unnamed"}) at node '{reference.NodeName}'.");
                }
                if (warnings.Count > 0) errors.Add($"Preflight failed for '{name}': {warnings[0]}");
                items.Add(new WorkflowDeploymentItem(path, name, targetId, string.IsNullOrWhiteSpace(targetId) ? "create" : "update", warnings));
            }
            catch (JsonException) { errors.Add($"Workflow '{path}' is not valid JSON."); }
        }
        return new WorkflowDeploymentPreview(source.Environment.Key, target.Environment.Key, items, errors);
    }

    private static async Task<DateTimeOffset?> GetLiveUpdatedAtAsync(HttpClient client, string baseUrl, string path, string workflowId, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), $"{path.Trim('/')}" + "/" + Uri.EscapeDataString(workflowId)));
        request.Headers.Add("X-N8N-API-KEY", apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new WorkflowImportException($"Conflict check could not fetch live target workflow '{workflowId}' ({(int)response.StatusCode}).");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return json.RootElement.TryGetProperty("updatedAt", out var value) && DateTimeOffset.TryParse(value.GetString(), out var timestamp) ? timestamp : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonNode? node) => node is not null && DateTimeOffset.TryParse(node.GetValue<string>(), out var timestamp) ? timestamp : null;

    public async Task<WorkflowDeploymentResult> DeployAsync(WorkflowDeploymentApplyRequest request, CancellationToken cancellationToken)
    {
        if (!request.Confirmation) throw new WorkflowImportException("Explicit confirmation is required before live workflow deployment.");
        var preview = await PreviewAsync(new WorkflowDeploymentPreviewRequest(request.SourceEnvironmentKey, request.TargetEnvironmentKey, request.WorkflowFilePaths), cancellationToken);
        if (preview.BlockingErrors.Count > 0) throw new WorkflowImportException(string.Join(" ", preview.BlockingErrors));
        var source = await environments.GetByKeyAsync(request.SourceEnvironmentKey, cancellationToken);
        var target = await environments.GetByKeyAsync(request.TargetEnvironmentKey, cancellationToken);
        var config = await db.EnvironmentN8nApiConfigs.AsNoTracking().SingleAsync(x => x.EnvironmentId == target.Environment.Id, cancellationToken);
        var apiKey = await configStore.GetApiKeyAsync(target.Environment.Key, cancellationToken) ?? throw new WorkflowImportException("Missing target API key.");
        var sourceFiles = git.ReadWorkflowFilesFromBranch(source.Workspace.RepoPath, source.Environment.GitBranch);
        var client = httpClientFactory.CreateClient("n8n");
        var created = new List<string>(); var updated = new List<string>(); var warnings = new List<string>();
        foreach (var workflow in preview.Workflows)
        {
            var body = JsonNode.Parse(sourceFiles[workflow.WorkflowFilePath])?.AsObject() ?? throw new WorkflowImportException($"Workflow '{workflow.WorkflowFilePath}' is invalid.");
            body.Remove("id"); body.Remove("staticData"); body.Remove("pinData"); body["active"] = false; // activation is a distinct, opt-in n8n operation
            var path = string.IsNullOrWhiteSpace(workflow.TargetWorkflowId) ? config.WorkflowApiPath : $"{config.WorkflowApiPath.TrimEnd('/')}/{Uri.EscapeDataString(workflow.TargetWorkflowId)}";
            using var message = new HttpRequestMessage(string.IsNullOrWhiteSpace(workflow.TargetWorkflowId) ? HttpMethod.Post : HttpMethod.Put, new Uri(new Uri(config.BaseUrl + "/"), path.TrimStart('/')));
            message.Headers.Add("X-N8N-API-KEY", apiKey);
            message.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(message, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new WorkflowImportException($"Deployment of '{workflow.Name}' failed ({(int)response.StatusCode}): {responseBody[..Math.Min(500, responseBody.Length)]}. No remaining workflows were deployed.");
            var id = workflow.TargetWorkflowId;
            if (string.IsNullOrWhiteSpace(id)) { using var document = JsonDocument.Parse(responseBody); id = document.RootElement.TryGetProperty("id", out var found) ? found.GetString() : workflow.WorkflowFilePath; created.Add(id ?? workflow.WorkflowFilePath); }
            else updated.Add(id);
            if (request.ActivateWorkflows) warnings.Add($"'{workflow.Name}' was deployed inactive. Activation remains a separate n8n safety action.");
        }
        await auditService.RecordAsync(new PromotionAuditCreate(target.Workspace.Id, source.Environment.Id, source.Environment.Key, target.Environment.Id, target.Environment.Key, "workflow-deployed", null, JsonSerializer.Serialize(new { Created = created, Updated = updated, ActivateRequested = request.ActivateWorkflows }), DateTimeOffset.UtcNow), cancellationToken);
        return new WorkflowDeploymentResult(target.Environment.Key, created, updated, warnings);
    }
}
