using System.Text.Json;
using Application.Contracts;
using Application.Models;

namespace Application;

public sealed class AiCredentialMappingService
{
    private const string SystemPrompt = """
        You are an AI assistant inside n8n Move Manager.

        Your task is to match environment credential references that represent the same real-world credential across two n8n environments.

        Safety rules:
        * You receive credential metadata only. Never ask for or infer secret values.
        * Return JSON only.
        * Only use credential IDs that are present in the provided sourceCredentials and targetCredentials arrays.
        * Prefer mappings where credentialType matches exactly.
        * Use a concise stable logicalKey made from the real-world credential purpose, not the environment name.
        * Use a human-readable displayName.
        * If unsure, do not map the pair.

        Return this JSON shape:
        {
          "mappings": [
            {
              "logicalKey": "slack-bot",
              "displayName": "Slack bot",
              "sourceEnvironmentCredentialId": "guid from sourceCredentials",
              "targetEnvironmentCredentialId": "guid from targetCredentials",
              "reason": "short reason",
              "confidence": "low|medium|high"
            }
          ],
          "warnings": []
        }
        """;

    private readonly IAiProviderSettingsStore _settingsStore;
    private readonly IAiAgentClient _agentClient;
    private readonly ICredentialInventoryService _credentialInventoryService;
    private readonly ILogicalCredentialService _logicalCredentialService;

    public AiCredentialMappingService(
        IAiProviderSettingsStore settingsStore,
        IAiAgentClient agentClient,
        ICredentialInventoryService credentialInventoryService,
        ILogicalCredentialService logicalCredentialService)
    {
        _settingsStore = settingsStore;
        _agentClient = agentClient;
        _credentialInventoryService = credentialInventoryService;
        _logicalCredentialService = logicalCredentialService;
    }

    public async Task<AiCredentialMappingResult> CreateMappingsAsync(AiCredentialMappingRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourceEnvironmentKey)
            || string.IsNullOrWhiteSpace(request.TargetEnvironmentKey))
        {
            throw new WorkflowImportException("Source and target environments are required.");
        }

        if (string.Equals(request.SourceEnvironmentKey, request.TargetEnvironmentKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowImportException("Source and target environments must be different.");
        }

        var sourceCredentials = await _credentialInventoryService.ListEnvironmentCredentialsAsync(request.SourceEnvironmentKey, cancellationToken);
        var targetCredentials = await _credentialInventoryService.ListEnvironmentCredentialsAsync(request.TargetEnvironmentKey, cancellationToken);
        if (sourceCredentials.Count == 0 || targetCredentials.Count == 0)
        {
            throw new WorkflowImportException("Both environments need detected credentials before AI can create mappings.");
        }

        var existingLogicalCredentials = (await _logicalCredentialService.ListAsync(cancellationToken)).ToList();
        var configuration = await RequireConfigurationAsync(cancellationToken);
        var prompt = BuildPrompt(request, sourceCredentials, targetCredentials, existingLogicalCredentials);
        var raw = await _agentClient.RunJsonAsync(configuration, SystemPrompt, prompt, cancellationToken);
        var aiPlan = ParsePlan(raw);

        var sourceById = sourceCredentials.ToDictionary(credential => credential.Id);
        var targetById = targetCredentials.ToDictionary(credential => credential.Id);
        var logicalByKey = existingLogicalCredentials.ToDictionary(credential => credential.Key, StringComparer.OrdinalIgnoreCase);
        var items = new List<AiCredentialMappingAppliedItem>();
        var warnings = aiPlan.Warnings.ToList();
        var createdLogicalCredentialsCount = 0;
        var seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var suggestion in aiPlan.Mappings)
        {
            var key = Slugify(suggestion.LogicalKey);
            var displayName = string.IsNullOrWhiteSpace(suggestion.DisplayName) ? key : suggestion.DisplayName.Trim();
            var confidence = NormalizeConfidence(suggestion.Confidence);
            var sourceId = suggestion.SourceEnvironmentCredentialId;
            var targetId = suggestion.TargetEnvironmentCredentialId;
            var sourceLabel = sourceById.TryGetValue(sourceId, out var sourceCredential)
                ? CredentialLabel(sourceCredential)
                : sourceId.ToString();
            var targetLabel = targetById.TryGetValue(targetId, out var targetCredential)
                ? CredentialLabel(targetCredential)
                : targetId.ToString();
            string? skippedReason = null;

            if (string.IsNullOrWhiteSpace(key))
            {
                skippedReason = "AI returned an empty logical credential key.";
            }
            else if (sourceCredential is null)
            {
                skippedReason = "AI returned a source credential ID that does not exist in the selected source environment.";
            }
            else if (targetCredential is null)
            {
                skippedReason = "AI returned a target credential ID that does not exist in the selected target environment.";
            }
            else if (!string.Equals(sourceCredential.CredentialType, targetCredential.CredentialType, StringComparison.OrdinalIgnoreCase))
            {
                skippedReason = $"Credential types differ: {sourceCredential.CredentialType} vs {targetCredential.CredentialType}.";
            }
            else if (!seenPairs.Add($"{sourceId:N}:{targetId:N}"))
            {
                skippedReason = "AI returned this credential pair more than once.";
            }

            if (skippedReason is not null)
            {
                items.Add(new AiCredentialMappingAppliedItem(
                    key,
                    displayName,
                    null,
                    sourceId,
                    targetId,
                    sourceLabel,
                    targetLabel,
                    suggestion.Reason,
                    confidence,
                    false,
                    skippedReason));
                continue;
            }

            if (!logicalByKey.TryGetValue(key, out var logicalCredential))
            {
                logicalCredential = await _logicalCredentialService.CreateAsync(new LogicalCredentialRequest(key, displayName), cancellationToken);
                logicalByKey[key] = logicalCredential;
                createdLogicalCredentialsCount++;
            }

            await _logicalCredentialService.SetMappingAsync(
                new LogicalCredentialMappingRequest(logicalCredential.Id, request.SourceEnvironmentKey, sourceId),
                cancellationToken);
            var mapped = await _logicalCredentialService.SetMappingAsync(
                new LogicalCredentialMappingRequest(logicalCredential.Id, request.TargetEnvironmentKey, targetId),
                cancellationToken);
            logicalByKey[key] = mapped;

            items.Add(new AiCredentialMappingAppliedItem(
                key,
                displayName,
                logicalCredential.Id,
                sourceId,
                targetId,
                sourceLabel,
                targetLabel,
                suggestion.Reason,
                confidence,
                true,
                null));
        }

        if (items.Count == 0)
        {
            warnings.Add("AI did not return any credential mappings to apply.");
        }

        return new AiCredentialMappingResult(
            request.SourceEnvironmentKey,
            request.TargetEnvironmentKey,
            aiPlan.Mappings.Count,
            items.Count(item => item.Applied),
            createdLogicalCredentialsCount,
            items,
            warnings);
    }

    private async Task<AiProviderConfiguration> RequireConfigurationAsync(CancellationToken cancellationToken)
    {
        var configuration = await _settingsStore.GetConfigurationAsync(cancellationToken);
        if (configuration is null || !configuration.Enabled)
        {
            throw new WorkflowImportException("AI assistant is disabled or provider settings are not configured.");
        }

        if (string.IsNullOrWhiteSpace(configuration.Endpoint)
            || string.IsNullOrWhiteSpace(configuration.ModelName)
            || string.IsNullOrWhiteSpace(configuration.ApiKey))
        {
            throw new WorkflowImportException("AI provider is not configured. Add endpoint, model, and API key first.");
        }

        return configuration;
    }

    private static string BuildPrompt(
        AiCredentialMappingRequest request,
        IReadOnlyList<EnvironmentCredentialDto> sourceCredentials,
        IReadOnlyList<EnvironmentCredentialDto> targetCredentials,
        IReadOnlyList<LogicalCredentialDto> existingLogicalCredentials)
    {
        return JsonSerializer.Serialize(new
        {
            instruction = "Create only the needed logical credential mappings between sourceCredentials and targetCredentials. Return JSON only.",
            sourceEnvironmentKey = request.SourceEnvironmentKey,
            targetEnvironmentKey = request.TargetEnvironmentKey,
            sourceCredentials = sourceCredentials.Select(ToAiCredential),
            targetCredentials = targetCredentials.Select(ToAiCredential),
            existingLogicalCredentials = existingLogicalCredentials.Select(credential => new
            {
                credential.Id,
                credential.Key,
                credential.DisplayName,
                mappings = credential.Mappings.Select(mapping => new
                {
                    mapping.EnvironmentKey,
                    mapping.EnvironmentCredentialId,
                    mapping.CredentialType,
                    mapping.CredentialId,
                    mapping.CredentialName
                })
            })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static object ToAiCredential(EnvironmentCredentialDto credential)
    {
        return new
        {
            credential.Id,
            credential.EnvironmentKey,
            credential.CredentialType,
            credential.CredentialId,
            credential.CredentialName,
            credential.ReferenceCount
        };
    }

    private static AiMappingPlan ParsePlan(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractJson(raw));
            var root = document.RootElement;
            var mappings = new List<AiMappingSuggestion>();
            if (root.TryGetProperty("mappings", out var mappingArray) && mappingArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in mappingArray.EnumerateArray())
                {
                    if (!TryReadGuid(item, "sourceEnvironmentCredentialId", out var sourceId)
                        || !TryReadGuid(item, "targetEnvironmentCredentialId", out var targetId))
                    {
                        continue;
                    }

                    mappings.Add(new AiMappingSuggestion(
                        ReadString(item, "logicalKey") ?? string.Empty,
                        ReadString(item, "displayName") ?? string.Empty,
                        sourceId,
                        targetId,
                        ReadString(item, "reason") ?? string.Empty,
                        ReadString(item, "confidence") ?? "low"));
                }
            }

            return new AiMappingPlan(mappings, ReadStringArray(root, "warnings"));
        }
        catch (JsonException)
        {
            throw new WorkflowImportException("AI provider returned mapping output that was not valid JSON.");
        }
    }

    private static string ExtractJson(string raw)
    {
        var trimmed = raw.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
    }

    private static bool TryReadGuid(JsonElement element, string propertyName, out Guid value)
    {
        value = Guid.Empty;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out value);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    private static string NormalizeConfidence(string? confidence)
    {
        return string.Equals(confidence, "high", StringComparison.OrdinalIgnoreCase)
            || string.Equals(confidence, "medium", StringComparison.OrdinalIgnoreCase)
            || string.Equals(confidence, "low", StringComparison.OrdinalIgnoreCase)
                ? confidence!.ToLowerInvariant()
                : "low";
    }

    private static string CredentialLabel(EnvironmentCredentialDto credential)
    {
        return $"{credential.CredentialName ?? credential.CredentialId ?? "Unnamed"} / {credential.CredentialType}";
    }

    private static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record AiMappingPlan(
        IReadOnlyList<AiMappingSuggestion> Mappings,
        IReadOnlyList<string> Warnings);

    private sealed record AiMappingSuggestion(
        string LogicalKey,
        string DisplayName,
        Guid SourceEnvironmentCredentialId,
        Guid TargetEnvironmentCredentialId,
        string Reason,
        string Confidence);
}
