using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Application;
using Application.Contracts;
using Application.Models;
using Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;

namespace Infrastructure;

public sealed class DataTableService : IDataTableService
{
    private readonly AppDbContext _dbContext;
    private readonly IEnvironmentService _environmentService;
    private readonly IGitRepositoryService _gitRepositoryService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEnvironmentN8nApiConfigStore _configStore;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public DataTableService(AppDbContext dbContext, IEnvironmentService environmentService, IGitRepositoryService gitRepositoryService, IHttpClientFactory httpClientFactory, IEnvironmentN8nApiConfigStore configStore, IHttpContextAccessor httpContextAccessor)
    {
        _dbContext = dbContext;
        _environmentService = environmentService;
        _gitRepositoryService = gitRepositoryService;
        _httpClientFactory = httpClientFactory;
        _configStore = configStore;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<PagedResult<DataTableListItemDto>> ListAsync(string environmentKey, int page, int pageSize, string? search, string? sort, string? direction, CancellationToken cancellationToken)
    {
        var key = environmentKey.Trim().ToLowerInvariant();
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _dbContext.DataTableSnapshots.AsNoTracking().Where(item => item.EnvironmentKey == key);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(item => EF.Functions.Like(item.Name, pattern) || EF.Functions.Like(item.ExternalId, pattern));
        }
        var descending = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
        query = (sort ?? "name").ToLowerInvariant() switch
        {
            "rows" or "rowcount" => descending ? query.OrderByDescending(item => item.RowCount).ThenBy(item => item.Name) : query.OrderBy(item => item.RowCount).ThenBy(item => item.Name),
            "synced" => descending ? query.OrderByDescending(item => item.LastSyncedAt) : query.OrderBy(item => item.LastSyncedAt),
            _ => descending ? query.OrderByDescending(item => item.Name) : query.OrderBy(item => item.Name)
        };
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new DataTableListItemDto(item.ExternalId, item.Name, item.ColumnsJson, item.RowCount, item.EnvironmentKey, item.LastSyncedAt))
            .ToArrayAsync(cancellationToken);
        return new PagedResult<DataTableListItemDto>(items, totalCount, page, pageSize);
    }

    public async Task<DataTableSyncResult> SyncAsync(string environmentKey, CancellationToken cancellationToken)
    {
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        var config = await _dbContext.EnvironmentN8nApiConfigs.AsNoTracking().SingleOrDefaultAsync(item => item.EnvironmentId == context.Environment.Id, cancellationToken);
        if (config is null || !config.Enabled) throw new WorkflowImportException("Enable and save the n8n API connection before syncing Data Tables.");
        var apiKey = await _configStore.GetApiKeyAsync(environmentKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new WorkflowImportException("An n8n API key is required to sync Data Tables.");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(config.BaseUrl + "/"), config.DataTablesPath.TrimStart('/')));
        request.Headers.Add("X-N8N-API-KEY", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var client = _httpClientFactory.CreateClient("n8n");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new WorkflowImportException($"n8n Data Tables request failed ({(int)response.StatusCode}): {body[..Math.Min(body.Length, 500)]}");

        using var document = JsonDocument.Parse(body);
        var remoteTables = GetArray(document.RootElement).Select(ParseRemoteTable).Where(item => item is not null).Cast<RemoteTable>().ToArray();
        var now = DateTimeOffset.UtcNow;
        var changedPaths = new List<string>();
        foreach (var remote in remoteTables)
        {
            var snapshot = await _dbContext.DataTableSnapshots.SingleOrDefaultAsync(item => item.EnvironmentId == context.Environment.Id && item.ExternalId == remote.Id, cancellationToken);
            var changed = snapshot is null || snapshot.Name != remote.Name || snapshot.ColumnsJson != remote.ColumnsJson || snapshot.RowCount != remote.RowCount;
            if (snapshot is null)
            {
                snapshot = new DataTableSnapshot { Id = Guid.NewGuid(), WorkspaceId = context.Workspace.Id, EnvironmentId = context.Environment.Id, EnvironmentKey = context.Environment.Key, ExternalId = remote.Id };
                _dbContext.DataTableSnapshots.Add(snapshot);
            }
            snapshot.Name = remote.Name;
            snapshot.ColumnsJson = remote.ColumnsJson;
            snapshot.RowCount = remote.RowCount;
            snapshot.LastSyncedAt = now;
            if (changed) changedPaths.Add(WriteSchemaFile(context.Workspace.RepoPath, remote, now));
        }
        await _dbContext.SaveChangesAsync(cancellationToken);
        _gitRepositoryService.EnsureBranch(context.Workspace.RepoPath, context.Environment.GitBranch);
        var commit = changedPaths.Count == 0
            ? new GitCommitResult(false, null, 0, "No schema changes were detected.", null)
            : await _gitRepositoryService.CommitChangesAsync(context.Workspace.RepoPath, context.Environment.Key, changedPaths, $"Sync Data Table schemas into {context.Environment.Key}", cancellationToken);
        return new DataTableSyncResult(context.Environment.Key, remoteTables.Length, changedPaths.Count, commit.CommitSha, !commit.CommitCreated, []);
    }

    public async Task<DataTableComparisonDto> CompareAsync(string sourceEnvironmentKey, string targetEnvironmentKey, CancellationToken cancellationToken)
    {
        var source = sourceEnvironmentKey.Trim().ToLowerInvariant();
        var target = targetEnvironmentKey.Trim().ToLowerInvariant();
        var tables = await _dbContext.DataTableSnapshots.AsNoTracking().Where(item => item.EnvironmentKey == source || item.EnvironmentKey == target).ToArrayAsync(cancellationToken);
        var sourceById = tables.Where(item => item.EnvironmentKey == source).ToDictionary(item => item.ExternalId, StringComparer.OrdinalIgnoreCase);
        var targetById = tables.Where(item => item.EnvironmentKey == target).ToDictionary(item => item.ExternalId, StringComparer.OrdinalIgnoreCase);
        var items = sourceById.Keys.Union(targetById.Keys, StringComparer.OrdinalIgnoreCase).Select(id =>
        {
            sourceById.TryGetValue(id, out var left);
            targetById.TryGetValue(id, out var right);
            var status = left is null ? "only-in-target" : right is null ? "only-in-source" : left.ColumnsJson != right.ColumnsJson ? "schema-changed" : left.RowCount != right.RowCount ? "row-count-changed" : "in-sync";
            return new DataTableComparisonItem(id, left?.Name ?? right!.Name, status, left?.RowCount, right?.RowCount);
        }).OrderBy(item => item.Name).ToArray();
        return new DataTableComparisonDto(source, target, items);
    }

    public async Task<DataTablePromotionPlan> GetPromotionPlanAsync(string sourceEnvironmentKey, string targetEnvironmentKey, CancellationToken cancellationToken)
    {
        var comparison = await CompareAsync(sourceEnvironmentKey, targetEnvironmentKey, cancellationToken);
        var changes = comparison.Items.Where(item => item.Status != "in-sync").ToArray();
        return new DataTablePromotionPlan(comparison.SourceEnvironmentKey, comparison.TargetEnvironmentKey, changes, changes.Length,
            "This stages schema-only snapshots in the target environment's Git branch. It does not write rows or call a remote n8n write API.");
    }

    public async Task<DataTablePromotionApplyResult> ApplyPromotionAsync(DataTablePromotionApplyRequest request, CancellationToken cancellationToken)
    {
        if (!request.Confirmation) throw new WorkflowImportException("Confirmation is required to stage a Data Table promotion.");
        var sourceContext = await _environmentService.GetByKeyAsync(request.SourceEnvironmentKey, cancellationToken);
        var targetContext = await _environmentService.GetByKeyAsync(request.TargetEnvironmentKey, cancellationToken);
        if (sourceContext.Environment.Id == targetContext.Environment.Id) throw new WorkflowImportException("Source and target environments must be different.");
        var requestedIds = request.TableIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requestedIds.Length == 0) throw new WorkflowImportException("Select at least one Data Table to stage.");
        var sourceTables = await _dbContext.DataTableSnapshots.Where(item => item.EnvironmentId == sourceContext.Environment.Id && requestedIds.Contains(item.ExternalId)).ToArrayAsync(cancellationToken);
        if (sourceTables.Length != requestedIds.Length) throw new WorkflowImportException("One or more selected Data Tables are no longer available in the source snapshot.");

        var now = DateTimeOffset.UtcNow;
        var paths = new List<string>();
        foreach (var source in sourceTables)
        {
            var target = await _dbContext.DataTableSnapshots.SingleOrDefaultAsync(item => item.EnvironmentId == targetContext.Environment.Id && item.ExternalId == source.ExternalId, cancellationToken);
            var changed = target is null || target.Name != source.Name || target.ColumnsJson != source.ColumnsJson || target.RowCount != source.RowCount;
            if (target is null)
            {
                target = new DataTableSnapshot { Id = Guid.NewGuid(), WorkspaceId = targetContext.Workspace.Id, EnvironmentId = targetContext.Environment.Id, EnvironmentKey = targetContext.Environment.Key, ExternalId = source.ExternalId };
                _dbContext.DataTableSnapshots.Add(target);
            }
            target.Name = source.Name;
            target.ColumnsJson = source.ColumnsJson;
            target.RowCount = source.RowCount;
            target.LastSyncedAt = now;
            if (changed) paths.Add(WriteSchemaFile(targetContext.Workspace.RepoPath, new RemoteTable(source.ExternalId, source.Name, source.ColumnsJson, source.RowCount), now));
        }
        await _dbContext.SaveChangesAsync(cancellationToken);
        _gitRepositoryService.EnsureBranch(targetContext.Workspace.RepoPath, targetContext.Environment.GitBranch);
        var commit = paths.Count == 0 ? new GitCommitResult(false, null, 0, "No schema changes were detected.", null) : await _gitRepositoryService.CommitChangesAsync(targetContext.Workspace.RepoPath, targetContext.Environment.Key, paths, $"Stage Data Table schemas from {sourceContext.Environment.Key} to {targetContext.Environment.Key}", cancellationToken);
        return new DataTablePromotionApplyResult(sourceContext.Environment.Key, targetContext.Environment.Key, paths.Count, commit.CommitSha, !commit.CommitCreated);
    }

    public async Task<DataTableLiveDeployResult> DeploySchemasAsync(DataTableLiveDeployRequest request, CancellationToken cancellationToken)
    {
        if (!request.Confirmation) throw new WorkflowImportException("Confirmation is required for a live schema deployment.");
        var source = await _environmentService.GetByKeyAsync(request.SourceEnvironmentKey, cancellationToken);
        var target = await _environmentService.GetByKeyAsync(request.TargetEnvironmentKey, cancellationToken);
        var config = await _dbContext.EnvironmentN8nApiConfigs.AsNoTracking().SingleOrDefaultAsync(item => item.EnvironmentId == target.Environment.Id, cancellationToken);
        var apiKey = await _configStore.GetApiKeyAsync(request.TargetEnvironmentKey, cancellationToken);
        if (config is null || !config.Enabled || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(config.DataTablesWritePathTemplate)) throw new WorkflowImportException("The target requires an enabled n8n API connection, API key, and a configured Data Tables write-path template containing {id}.");
        if (!config.DataTablesWritePathTemplate.Contains("{id}", StringComparison.Ordinal)) throw new WorkflowImportException("The Data Tables write-path template must contain {id}.");
        var ids = request.TableIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tables = await _dbContext.DataTableSnapshots.AsNoTracking().Where(item => item.EnvironmentId == source.Environment.Id && ids.Contains(item.ExternalId)).ToArrayAsync(cancellationToken);
        if (tables.Length != ids.Length) throw new WorkflowImportException("One or more selected source snapshots are missing.");
        var client = _httpClientFactory.CreateClient("n8n");
        foreach (var table in tables)
        {
            var path = config.DataTablesWritePathTemplate.Replace("{id}", Uri.EscapeDataString(table.ExternalId), StringComparison.Ordinal);
            using var message = new HttpRequestMessage(HttpMethod.Put, new Uri(new Uri(config.BaseUrl + "/"), path.TrimStart('/')));
            message.Headers.Add("X-N8N-API-KEY", apiKey);
            using var columns = JsonDocument.Parse(table.ColumnsJson);
            message.Content = JsonContent.Create(new { name = table.Name, columns = columns.RootElement });
            using var response = await client.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new WorkflowImportException($"Live deployment of '{table.Name}' failed with {(int)response.StatusCode}. No further tables were deployed.");
        }
        var deployedIds = tables.Select(table => table.ExternalId).ToArray();
        _dbContext.DataTableDeploymentAudits.Add(new DataTableDeploymentAudit { Id = Guid.NewGuid(), WorkspaceId = target.Workspace.Id, SourceEnvironmentKey = source.Environment.Key, TargetEnvironmentKey = target.Environment.Key, Status = "succeeded", TableIdsJson = JsonSerializer.Serialize(deployedIds), ActorUserName = _httpContextAccessor.HttpContext?.User.Identity?.Name, CreatedAt = DateTimeOffset.UtcNow });
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new DataTableLiveDeployResult(target.Environment.Key, tables.Length, deployedIds);
    }

    private static IEnumerable<JsonElement> GetArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToArray();
        foreach (var property in new[] { "data", "items" }) if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().ToArray();
        throw new WorkflowImportException("The n8n response did not contain a Data Tables array. Adjust the configured Data Tables API path for this n8n version.");
    }

    private static RemoteTable? ParseRemoteTable(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var id = GetString(item, "id") ?? GetString(item, "tableId");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var name = GetString(item, "name") ?? id;
        var columns = item.TryGetProperty("columns", out var found) ? found : item.TryGetProperty("schema", out found) ? found : default;
        var columnsJson = columns.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "[]" : JsonSerializer.Serialize(columns);
        var rowCount = GetInt(item, "rowCount") ?? GetInt(item, "rowsCount") ?? (item.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array ? rows.GetArrayLength() : null);
        return new RemoteTable(id, name, columnsJson, rowCount);
    }

    private static string WriteSchemaFile(string repoPath, RemoteTable table, DateTimeOffset syncedAt)
    {
        var name = string.Concat(table.Name.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')).Trim('-');
        if (string.IsNullOrWhiteSpace(name)) name = table.Id;
        var relativePath = $"data-tables/{name}-{table.Id[..Math.Min(table.Id.Length, 8)]}.schema.json";
        var fullPath = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var content = JsonSerializer.Serialize(new { id = table.Id, name = table.Name, columns = JsonDocument.Parse(table.ColumnsJson).RootElement, rowCount = table.RowCount, snapshotKind = "schema-only", syncedAt }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(fullPath, content + Environment.NewLine, Encoding.UTF8);
        return relativePath;
    }

    private static string? GetString(JsonElement item, string property) => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? GetInt(JsonElement item, string property) => item.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;
    private sealed record RemoteTable(string Id, string Name, string ColumnsJson, int? RowCount);
}
