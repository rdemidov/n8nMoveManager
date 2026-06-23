using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Application.Models;

namespace Application;

public sealed class AiContextBuilder
{
    private const int MaxStringLength = 700;
    private const int MaxArrayItems = 80;
    private static readonly Regex SecretPattern = new(
        "(api[_-]?key|token|secret|password|passwd|authorization|bearer|client[_-]?secret|access[_-]?token|refresh[_-]?token|private[_-]?key|credentialData)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SecretValuePattern = new(
        "(sk-[A-Za-z0-9_-]{12,}|xox[baprs]-[A-Za-z0-9-]{12,}|gh[pousr]_[A-Za-z0-9_]{12,}|Bearer\\s+[A-Za-z0-9._~+/=-]{12,}|[A-Za-z0-9+/]{32,}={0,2})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AiContextEnvelope BuildWorkflowDiff(AiWorkflowDiffRequest request)
    {
        var context = new JsonObject
        {
            ["environmentKey"] = CleanString(request.EnvironmentKey),
            ["sourceEnvironmentKey"] = CleanString(request.SourceEnvironmentKey),
            ["targetEnvironmentKey"] = CleanString(request.TargetEnvironmentKey),
            ["workflowFilePath"] = CleanString(request.WorkflowFilePath),
            ["workflowId"] = CleanString(request.WorkflowId),
            ["diff"] = ToSafeNode(request.DiffContext)
        };

        return new AiContextEnvelope("workflow-diff", context, ["credential secret-like values masked", "long values truncated"]);
    }

    public AiContextEnvelope BuildPromotionPlan(AiPromotionPlanRequest request)
    {
        var context = new JsonObject
        {
            ["promotionPlan"] = ToSafeNode(request.PromotionPlan)
        };

        return new AiContextEnvelope("promotion-plan", context, ["credential references only", "long values truncated"]);
    }

    public AiContextEnvelope BuildConflict(AiConflictRequest request)
    {
        var context = new JsonObject
        {
            ["sourceEnvironmentKey"] = CleanString(request.SourceEnvironmentKey),
            ["targetEnvironmentKey"] = CleanString(request.TargetEnvironmentKey),
            ["workflowChange"] = ToSafeNode(request.WorkflowChange),
            ["workflowDiff"] = ToSafeNode(request.WorkflowDiff),
            ["promotionPlan"] = ToSafeNode(request.PromotionPlan)
        };

        return new AiContextEnvelope("conflict", context, ["credential references only", "secret-like values masked"]);
    }

    public AiContextEnvelope BuildAsk(AiAskRequest request)
    {
        var context = new JsonObject
        {
            ["question"] = CleanString(request.Question),
            ["scope"] = CleanString(request.Scope),
            ["environmentKey"] = CleanString(request.EnvironmentKey),
            ["sourceEnvironmentKey"] = CleanString(request.SourceEnvironmentKey),
            ["targetEnvironmentKey"] = CleanString(request.TargetEnvironmentKey),
            ["workflowFilePath"] = CleanString(request.WorkflowFilePath),
            ["workflowId"] = CleanString(request.WorkflowId),
            ["diff"] = ToSafeNode(request.DiffContext),
            ["promotionPlan"] = ToSafeNode(request.PromotionPlan)
        };

        return new AiContextEnvelope("ask", context, ["credential references only", "secret-like values masked"]);
    }

    public JsonNode? ToSafeNode<T>(T value)
    {
        if (value is null)
        {
            return null;
        }

        var node = JsonSerializer.SerializeToNode(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return SanitizeNode(node, null);
    }

    private static JsonNode? SanitizeNode(JsonNode? node, string? propertyName)
    {
        if (node is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(propertyName) && IsSecretKey(propertyName))
        {
            return "[masked secret]";
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return CleanString(text);
            }

            return JsonNode.Parse(node.ToJsonString());
        }

        if (node is JsonArray array)
        {
            var safeArray = new JsonArray();
            foreach (var item in array.Take(MaxArrayItems))
            {
                safeArray.Add(SanitizeNode(item?.DeepClone(), propertyName));
            }

            if (array.Count > MaxArrayItems)
            {
                safeArray.Add($"[truncated {array.Count - MaxArrayItems} more items]");
            }

            return safeArray;
        }

        var source = node.AsObject();
        var safeObject = new JsonObject();
        foreach (var property in source)
        {
            if (string.Equals(property.Key, "credentials", StringComparison.OrdinalIgnoreCase)
                && property.Value is JsonObject credentials)
            {
                safeObject[property.Key] = SanitizeCredentialReferences(credentials);
                continue;
            }

            safeObject[property.Key] = SanitizeNode(property.Value?.DeepClone(), property.Key);
        }

        return safeObject;
    }

    private static JsonNode SanitizeCredentialReferences(JsonObject credentials)
    {
        var safeCredentials = new JsonObject();
        foreach (var credential in credentials)
        {
            safeCredentials[credential.Key] = credential.Value switch
            {
                JsonObject obj => KeepCredentialReference(obj),
                JsonArray array => new JsonArray(array.OfType<JsonObject>().Select(KeepCredentialReference).Cast<JsonNode?>().ToArray()),
                _ => "[credential reference omitted]"
            };
        }

        return safeCredentials;
    }

    private static JsonObject KeepCredentialReference(JsonObject credential)
    {
        return new JsonObject
        {
            ["id"] = CleanString(credential["id"]?.GetValue<string>()),
            ["name"] = CleanString(credential["name"]?.GetValue<string>()),
            ["type"] = CleanString(credential["type"]?.GetValue<string>())
        };
    }

    private static string? CleanString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var masked = SecretValuePattern.Replace(value, "[masked secret]");
        return masked.Length <= MaxStringLength ? masked : string.Concat(masked.AsSpan(0, MaxStringLength), "... [truncated]");
    }

    private static bool IsSecretKey(string propertyName)
    {
        return SecretPattern.IsMatch(propertyName);
    }
}
