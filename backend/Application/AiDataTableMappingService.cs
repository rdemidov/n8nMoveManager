using System.Text.Json;
using Application.Contracts;
using Application.Models;

namespace Application;

public sealed class AiDataTableMappingService(
    IAiProviderSettingsStore settingsStore,
    IAiAgentClient agentClient,
    IDataTableService dataTableService)
{
    private const string SystemPrompt = """
        You are an AI assistant inside n8n Move Manager. Match Data Tables that represent the same logical table across two n8n environments.

        Rules:
        * Return JSON only.
        * Only use IDs present in sourceTables and targetTables.
        * Compare table names and column schemas. Different environment-specific IDs are expected.
        * Suggest mappings only for source tables that are not already mapped.
        * Do not map merely because row counts are similar.
        * If the name or schema evidence is ambiguous, do not map the table and add a warning.

        Return:
        {
          "mappings": [
            {
              "sourceTableId": "id from sourceTables",
              "targetTableId": "id from targetTables",
              "reason": "short reason",
              "confidence": "low|medium|high"
            }
          ],
          "warnings": []
        }
        """;

    public async Task<AiDataTableMappingResult> CreateMappingsAsync(AiDataTableMappingRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourceEnvironmentKey) || string.IsNullOrWhiteSpace(request.TargetEnvironmentKey))
            throw new WorkflowImportException("Source and target environments are required.");
        if (string.Equals(request.SourceEnvironmentKey, request.TargetEnvironmentKey, StringComparison.OrdinalIgnoreCase))
            throw new WorkflowImportException("Source and target environments must be different.");

        var sourceTables = await ListAllAsync(request.SourceEnvironmentKey, cancellationToken);
        var targetTables = await ListAllAsync(request.TargetEnvironmentKey, cancellationToken);
        if (sourceTables.Count == 0 || targetTables.Count == 0)
            throw new WorkflowImportException("Both environments need synced Data Table snapshots before AI can create mappings.");

        var existingMappings = await dataTableService.GetMappingsAsync(request.SourceEnvironmentKey, request.TargetEnvironmentKey, cancellationToken);
        var configuration = await settingsStore.GetConfigurationAsync(cancellationToken);
        if (configuration is null || !configuration.Enabled || string.IsNullOrWhiteSpace(configuration.Endpoint) || string.IsNullOrWhiteSpace(configuration.ModelName) || string.IsNullOrWhiteSpace(configuration.ApiKey))
            throw new WorkflowImportException("AI provider is not configured. Enable it and add endpoint, model, and API key first.");

        var prompt = JsonSerializer.Serialize(new
        {
            instruction = "Map only unambiguous logical Data Table counterparts. Return JSON only.",
            request.SourceEnvironmentKey,
            request.TargetEnvironmentKey,
            sourceTables = sourceTables.Select(ToAiTable),
            targetTables = targetTables.Select(ToAiTable),
            existingMappings = existingMappings.Select(mapping => new { mapping.SourceTableId, mapping.SourceTableName, mapping.TargetTableId, mapping.TargetTableName })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var plan = ParsePlan(await agentClient.RunJsonAsync(configuration, SystemPrompt, prompt, cancellationToken));

        var sourceById = sourceTables.ToDictionary(table => table.Id, StringComparer.OrdinalIgnoreCase);
        var targetById = targetTables.ToDictionary(table => table.Id, StringComparer.OrdinalIgnoreCase);
        var alreadyMapped = existingMappings.Select(mapping => mapping.SourceTableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<AiDataTableMappingAppliedItem>();
        var warnings = plan.Warnings.ToList();

        foreach (var suggestion in plan.Mappings)
        {
            sourceById.TryGetValue(suggestion.SourceTableId, out var source);
            targetById.TryGetValue(suggestion.TargetTableId, out var target);
            string? skippedReason = source is null
                ? "AI returned a source table ID that is not in the selected environment."
                : target is null
                    ? "AI returned a target table ID that is not in the selected environment."
                    : alreadyMapped.Contains(source.Id)
                        ? "The source table already has a saved mapping."
                        : !seenSources.Add(source.Id)
                            ? "AI returned more than one target for this source table."
                            : null;

            if (skippedReason is null)
            {
                await dataTableService.SaveMappingAsync(new DataTableMappingRequest(request.SourceEnvironmentKey, request.TargetEnvironmentKey, source!.Id, target!.Id), cancellationToken);
                alreadyMapped.Add(source.Id);
            }

            items.Add(new AiDataTableMappingAppliedItem(
                suggestion.SourceTableId,
                suggestion.TargetTableId,
                source?.Name ?? suggestion.SourceTableId,
                target?.Name ?? suggestion.TargetTableId,
                suggestion.Reason,
                NormalizeConfidence(suggestion.Confidence),
                skippedReason is null,
                skippedReason));
        }

        if (items.Count == 0) warnings.Add("AI did not return any Data Table mappings to apply.");
        return new AiDataTableMappingResult(request.SourceEnvironmentKey, request.TargetEnvironmentKey, plan.Mappings.Count, items.Count(item => item.Applied), items, warnings);
    }

    private async Task<IReadOnlyList<DataTableListItemDto>> ListAllAsync(string environmentKey, CancellationToken cancellationToken)
    {
        var items = new List<DataTableListItemDto>();
        for (var page = 1; ; page++)
        {
            var result = await dataTableService.ListAsync(environmentKey, page, 100, null, "name", "asc", cancellationToken);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount) return items;
        }
    }

    private static object ToAiTable(DataTableListItemDto table)
    {
        object columns;
        try { columns = JsonSerializer.Deserialize<JsonElement>(table.ColumnsJson); }
        catch (JsonException) { columns = table.ColumnsJson; }
        return new { table.Id, table.Name, columns };
    }

    private static AiPlan ParsePlan(string raw)
    {
        try
        {
            var trimmed = raw.Trim();
            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            using var document = JsonDocument.Parse(start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed);
            var root = document.RootElement;
            var mappings = new List<AiSuggestion>();
            if (root.TryGetProperty("mappings", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var sourceId = ReadString(item, "sourceTableId");
                    var targetId = ReadString(item, "targetTableId");
                    if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId)) continue;
                    mappings.Add(new AiSuggestion(sourceId, targetId, ReadString(item, "reason") ?? string.Empty, ReadString(item, "confidence") ?? "low"));
                }
            }
            var warnings = root.TryGetProperty("warnings", out var warningArray) && warningArray.ValueKind == JsonValueKind.Array
                ? warningArray.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray()
                : [];
            return new AiPlan(mappings, warnings);
        }
        catch (JsonException)
        {
            throw new WorkflowImportException("AI provider returned Data Table mapping output that was not valid JSON.");
        }
    }

    private static string? ReadString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string NormalizeConfidence(string value) => value.Equals("high", StringComparison.OrdinalIgnoreCase) || value.Equals("medium", StringComparison.OrdinalIgnoreCase) ? value.ToLowerInvariant() : "low";
    private sealed record AiPlan(IReadOnlyList<AiSuggestion> Mappings, IReadOnlyList<string> Warnings);
    private sealed record AiSuggestion(string SourceTableId, string TargetTableId, string Reason, string Confidence);
}
