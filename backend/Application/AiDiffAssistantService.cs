using System.Text.Json;
using Application.Contracts;
using Application.Models;

namespace Application;

public sealed class AiDiffAssistantService
{
    private const string SystemPrompt = """
        You are an AI assistant inside an application called n8n Move Manager.

        Your role:
        Help users understand n8n workflow differences, promotion plans, credential mappings, and merge conflicts.

        You are not allowed to apply changes, deploy workflows, approve production moves, or make irreversible decisions. You only explain, summarize, detect risks, and suggest options.

        Rules:
        * Be concise and practical.
        * Explain changes in human-friendly language.
        * Focus on n8n workflow behavior, not raw JSON syntax.
        * Highlight risky changes clearly.
        * Treat production changes as requiring human review.
        * Never claim something is definitely safe unless the provided context proves it.
        * If context is incomplete, say what is missing.
        * Do not invent workflow behavior that is not present in the context.
        * Do not ask for or expose credential secret values.
        * Do not output decrypted credentials, tokens, passwords, API keys, or private keys.
        * If a value looks like a secret, refer to it as [masked secret].
        * Pay special attention to:
          * HTTP endpoints
          * authentication settings
          * credentials
          * SQL queries
          * webhook paths
          * Execute Workflow / sub-workflow references
          * active/inactive workflow status
          * trigger nodes
          * AI Agent tool nodes
          * code nodes
          * changed environment-specific values
          * deleted nodes
          * changed connections

        When suggesting a merge resolution, use exactly one of:
        * use-source
        * keep-target
        * skip
        * manual-review

        Always include:
        * summary
        * important changes
        * risks
        * suggested next step
        * confidence: low, medium, or high

        Return JSON only using this shape:
        {
          "answer": "short practical response",
          "summary": "brief summary",
          "importantChanges": ["human-friendly workflow behavior changes"],
          "risks": ["risk or review item"],
          "suggestedNextStep": "single next step",
          "confidence": "low|medium|high",
          "warnings": [],
          "blockingIssues": [],
          "citedContextItems": [],
          "suggestedResolution": "use-source|keep-target|skip|manual-review|null",
          "reasoning": "brief rationale"
        }
        """;

    private readonly IAiProviderSettingsStore _settingsStore;
    private readonly IAiAgentClient _agentClient;
    private readonly AiContextBuilder _contextBuilder;

    public AiDiffAssistantService(
        IAiProviderSettingsStore settingsStore,
        IAiAgentClient agentClient,
        AiContextBuilder contextBuilder)
    {
        _settingsStore = settingsStore;
        _agentClient = agentClient;
        _contextBuilder = contextBuilder;
    }

    public async Task<AiTestResult> TestAsync(CancellationToken cancellationToken)
    {
        var configuration = await RequireConfigurationAsync(cancellationToken);
        var response = await _agentClient.RunJsonAsync(
            configuration,
            SystemPrompt,
            "Respond with JSON confirming the connection works. Keep it short.",
            cancellationToken);
        return new AiTestResult(true, string.IsNullOrWhiteSpace(response) ? "AI provider responded." : "AI provider responded successfully.");
    }

    public Task<AiAssistantResponse> SummarizeWorkflowDiffAsync(AiWorkflowDiffRequest request, CancellationToken cancellationToken)
    {
        var context = _contextBuilder.BuildWorkflowDiff(request);
        return AskAsync("Summarize this workflow diff and call out risks.", context, cancellationToken);
    }

    public Task<AiAssistantResponse> SummarizePromotionPlanAsync(AiPromotionPlanRequest request, CancellationToken cancellationToken)
    {
        var context = _contextBuilder.BuildPromotionPlan(request);
        return AskAsync("Summarize this promotion plan, credentials affected, warnings, blocking issues, and next steps.", context, cancellationToken);
    }

    public Task<AiAssistantResponse> ExplainConflictAsync(AiConflictRequest request, CancellationToken cancellationToken)
    {
        var context = _contextBuilder.BuildConflict(request);
        return AskAsync("Explain this conflict. Suggest one of: use-source, keep-target, skip, manual-review.", context, cancellationToken);
    }

    public Task<AiAssistantResponse> AskAssistantAsync(AiAskRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            throw new WorkflowImportException("Question is required.");
        }

        var context = _contextBuilder.BuildAsk(request);
        return AskAsync(request.Question, context, cancellationToken);
    }

    private async Task<AiAssistantResponse> AskAsync(string instruction, AiContextEnvelope context, CancellationToken cancellationToken)
    {
        var configuration = await RequireConfigurationAsync(cancellationToken);
        var prompt = JsonSerializer.Serialize(new
        {
            instruction,
            context.Kind,
            context.Redactions,
            context = context.Context
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var raw = await _agentClient.RunJsonAsync(configuration, SystemPrompt, prompt, cancellationToken);
        return ParseResponse(raw);
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

    private static AiAssistantResponse ParseResponse(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractJson(raw));
            var root = document.RootElement;
            var summary = ReadString(root, "summary") ?? ReadString(root, "shortSummary");
            var suggestedNextStep = ReadString(root, "suggestedNextStep") ?? ReadString(root, "recommendedNextStep");
            var recommendedNextSteps = ReadStringArray(root, "recommendedNextSteps");
            if (!string.IsNullOrWhiteSpace(suggestedNextStep))
            {
                recommendedNextSteps = recommendedNextSteps.Count == 0
                    ? [suggestedNextStep]
                    : recommendedNextSteps.Prepend(suggestedNextStep).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }

            return new AiAssistantResponse(
                ReadString(root, "answer") ?? summary ?? "The AI provider returned an empty answer.",
                summary,
                ReadString(root, "detailedSummary") ?? summary,
                ReadStringArray(root, "importantChanges", "important changes"),
                ReadStringArray(root, "risks"),
                ReadStringArray(root, "warnings"),
                ReadStringArray(root, "blockingIssues"),
                recommendedNextSteps,
                ReadStringArray(root, "citedContextItems"),
                NormalizeSuggestedResolution(ReadString(root, "suggestedResolution")),
                ReadString(root, "reasoning"),
                NormalizeConfidence(ReadString(root, "confidence")));
        }
        catch (JsonException)
        {
            return new AiAssistantResponse(raw, null, null, [], [], ["AI provider returned non-JSON text."], [], [], [], null, null, "low");
        }
    }

    private static string ExtractJson(string raw)
    {
        var trimmed = raw.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, params string[] propertyNames)
    {
        JsonElement property = default;
        var found = false;
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out property))
            {
                found = true;
                break;
            }
        }

        if (!found || property.ValueKind != JsonValueKind.Array)
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

    private static string? NormalizeSuggestedResolution(string? resolution)
    {
        return string.Equals(resolution, "use-source", StringComparison.OrdinalIgnoreCase)
            || string.Equals(resolution, "keep-target", StringComparison.OrdinalIgnoreCase)
            || string.Equals(resolution, "skip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(resolution, "manual-review", StringComparison.OrdinalIgnoreCase)
                ? resolution!.ToLowerInvariant()
                : null;
    }
}
