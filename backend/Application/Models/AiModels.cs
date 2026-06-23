using System.Text.Json.Nodes;

namespace Application.Models;

public sealed record AiSettingsDto(
    bool Enabled,
    string Endpoint,
    string ModelName,
    bool HasApiKey,
    string StorageWarning);

public sealed record AiSettingsRequest(
    bool Enabled,
    string? Endpoint,
    string? ModelName,
    string? ApiKey);

public sealed record AiTestResult(
    bool Success,
    string Message);

public sealed record AiWorkflowDiffRequest(
    string? EnvironmentKey,
    string? SourceEnvironmentKey,
    string? TargetEnvironmentKey,
    string? WorkflowFilePath,
    string? WorkflowId,
    WorkflowSemanticDiffCollectionDto? DiffContext);

public sealed record AiPromotionPlanRequest(
    PromotionPlanDto? PromotionPlan,
    bool SaveToAuditLog);

public sealed record AiConflictRequest(
    PromotionPlanDto? PromotionPlan,
    PromotionWorkflowChangeDto? WorkflowChange,
    WorkflowSemanticDiffDto? WorkflowDiff,
    string? SourceEnvironmentKey,
    string? TargetEnvironmentKey);

public sealed record AiAskRequest(
    string? Question,
    string? Scope,
    string? EnvironmentKey,
    string? SourceEnvironmentKey,
    string? TargetEnvironmentKey,
    string? WorkflowFilePath,
    string? WorkflowId,
    WorkflowSemanticDiffCollectionDto? DiffContext,
    PromotionPlanDto? PromotionPlan);

public sealed record AiAssistantResponse(
    string Answer,
    string? ShortSummary,
    string? DetailedSummary,
    IReadOnlyList<string> ImportantChanges,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> RecommendedNextSteps,
    IReadOnlyList<string> CitedContextItems,
    string? SuggestedResolution,
    string? Reasoning,
    string Confidence);

public sealed record AiContextEnvelope(
    string Kind,
    JsonNode Context,
    IReadOnlyList<string> Redactions);
