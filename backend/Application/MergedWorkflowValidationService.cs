using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Models;

namespace Application;

public sealed class MergedWorkflowValidationService
{
    private readonly WorkflowCredentialScanner _credentialScanner;

    public MergedWorkflowValidationService(WorkflowCredentialScanner credentialScanner)
    {
        _credentialScanner = credentialScanner;
    }

    public ManualMergeValidationResult Validate(
        string workflowFilePath,
        string resultWorkflowJson,
        string sourceEnvironmentKey,
        string targetEnvironmentKey,
        IReadOnlyList<CredentialEnvironmentPair> mappings,
        IReadOnlyCollection<CredentialScanItem> selectedSourceCredentials,
        JsonObject? sourceWorkflow,
        JsonObject? targetWorkflow)
    {
        var warnings = new List<string>();
        var blockingErrors = new List<string>();
        var infoMessages = new List<string>();
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(resultWorkflowJson);
        }
        catch (JsonException ex)
        {
            return new ManualMergeValidationResult([], [$"Result workflow JSON is invalid: {ex.Message}"], []);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                blockingErrors.Add("Result workflow must be a JSON object.");
                return new ManualMergeValidationResult(warnings, blockingErrors, infoMessages);
            }

            if (!root.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            {
                blockingErrors.Add("Result workflow must include a nodes array.");
                return new ManualMergeValidationResult(warnings, blockingErrors, infoMessages);
            }

            var nodeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes.EnumerateArray())
            {
                var nodeName = ReadString(node, "name");
                if (string.IsNullOrWhiteSpace(nodeName))
                {
                    blockingErrors.Add("Every result node must have a name.");
                    continue;
                }

                if (!nodeNames.Add(nodeName))
                {
                    blockingErrors.Add($"Result workflow has duplicate node name '{nodeName}'.");
                }
            }

            ValidateConnections(root, nodeNames, blockingErrors);
            ValidateCredentials(workflowFilePath, root, sourceEnvironmentKey, targetEnvironmentKey, mappings, selectedSourceCredentials, blockingErrors, warnings);
            AddWorkflowWarnings(root, sourceWorkflow, targetWorkflow, nodeNames, warnings, infoMessages);
        }

        return new ManualMergeValidationResult(
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            blockingErrors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            infoMessages.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void ValidateConnections(JsonElement root, HashSet<string> nodeNames, List<string> blockingErrors)
    {
        if (!root.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var source in connections.EnumerateObject())
        {
            if (!nodeNames.Contains(source.Name))
            {
                blockingErrors.Add($"Connection source '{source.Name}' does not exist in the result nodes.");
            }

            foreach (var target in EnumerateConnectionTargets(source.Value, insideConnectionItem: false))
            {
                if (!nodeNames.Contains(target))
                {
                    blockingErrors.Add($"Connection target '{target}' does not exist in the result nodes.");
                }
            }
        }
    }

    private void ValidateCredentials(
        string workflowFilePath,
        JsonElement root,
        string sourceEnvironmentKey,
        string targetEnvironmentKey,
        IReadOnlyList<CredentialEnvironmentPair> mappings,
        IReadOnlyCollection<CredentialScanItem> selectedSourceCredentials,
        List<string> blockingErrors,
        List<string> warnings)
    {
        var sameEnvironment = string.Equals(sourceEnvironmentKey, targetEnvironmentKey, StringComparison.OrdinalIgnoreCase);
        foreach (var reference in selectedSourceCredentials)
        {
            var mapping = mappings.FirstOrDefault(item => Matches(item.Source, reference));
            if (!sameEnvironment && mapping is null)
            {
                blockingErrors.Add($"Missing target credential mapping for '{reference.CredentialName ?? reference.CredentialId}' ({reference.CredentialType}) on node '{reference.NodeName}'.");
                continue;
            }

            if (mapping is not null && !string.Equals(mapping.Source.CredentialType, mapping.Target.CredentialType, StringComparison.OrdinalIgnoreCase))
            {
                blockingErrors.Add($"Credential mapping '{mapping.LogicalKey}' changes type from '{mapping.Source.CredentialType}' to '{mapping.Target.CredentialType}'.");
            }
        }

        foreach (var reference in _credentialScanner.Scan(root, workflowFilePath, ReadString(root, "id"), ReadString(root, "name") ?? Path.GetFileNameWithoutExtension(workflowFilePath)))
        {
            var sourceMapping = mappings.FirstOrDefault(item => Matches(item.Source, reference));
            var targetMapping = mappings.FirstOrDefault(item => Matches(item.Target, reference));
            if (!sameEnvironment && sourceMapping is not null && targetMapping is null)
            {
                blockingErrors.Add($"Source-only credential '{reference.CredentialName ?? reference.CredentialId}' remains in the result on node '{reference.NodeName}'.");
            }

            if (reference.CredentialType.Contains("oauth", StringComparison.OrdinalIgnoreCase)
                || reference.CredentialType.Contains("api", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Node '{reference.NodeName}' uses authentication credential type '{reference.CredentialType}'. Review after merge.");
            }
        }
    }

    private static void AddWorkflowWarnings(
        JsonElement root,
        JsonObject? sourceWorkflow,
        JsonObject? targetWorkflow,
        HashSet<string> resultNodeNames,
        List<string> warnings,
        List<string> infoMessages)
    {
        if (targetWorkflow is not null
            && TryGetBool(targetWorkflow, "active", out var targetActive)
            && root.TryGetProperty("active", out var active)
            && active.ValueKind is JsonValueKind.True or JsonValueKind.False
            && active.GetBoolean() != targetActive)
        {
            warnings.Add($"Active workflow state changes from '{targetActive}' to '{active.GetBoolean()}'.");
        }

        foreach (var node in ReadNodes(targetWorkflow))
        {
            var name = ReadString(node, "name");
            var type = ReadString(node, "type") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name)
                && !resultNodeNames.Contains(name)
                && type.Contains("trigger", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Trigger node '{name}' is removed from the result.");
            }
        }

        var sourceWebhook = FindWebhookPath(sourceWorkflow);
        var targetWebhook = FindWebhookPath(targetWorkflow);
        if (!string.IsNullOrWhiteSpace(sourceWebhook)
            && !string.IsNullOrWhiteSpace(targetWebhook)
            && !string.Equals(sourceWebhook, targetWebhook, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Webhook path changes from '{targetWebhook}' to '{sourceWebhook}' if source webhook parameters are selected.");
        }

        if (FindExecuteWorkflowReference(root) is { } executeReference)
        {
            warnings.Add($"Execute Workflow reference '{executeReference}' should be verified in the target environment.");
        }

        infoMessages.Add("Result workflow was generated by backend selections; free-text JSON editing is not enabled.");
    }

    private static IEnumerable<string> EnumerateConnectionTargets(JsonElement element, bool insideConnectionItem)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("node", out var node) && node.ValueKind == JsonValueKind.String && node.GetString() is { } nodeName)
            {
                yield return nodeName;
                yield break;
            }

            foreach (var property in element.EnumerateObject())
            {
                foreach (var target in EnumerateConnectionTargets(property.Value, insideConnectionItem: insideConnectionItem || property.NameEquals("node")))
                {
                    yield return target;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var target in EnumerateConnectionTargets(item, insideConnectionItem))
                {
                    yield return target;
                }
            }
        }
        else if (insideConnectionItem && element.ValueKind == JsonValueKind.String && element.GetString() is { } value && !string.IsNullOrWhiteSpace(value))
        {
            yield return value;
        }
    }

    private static IEnumerable<JsonElement> ReadNodes(JsonObject? workflow)
    {
        if (workflow?["nodes"] is not JsonArray nodes)
        {
            yield break;
        }

        foreach (var node in nodes.OfType<JsonObject>())
        {
            using var document = JsonDocument.Parse(node.ToJsonString());
            yield return document.RootElement.Clone();
        }
    }

    private static string? FindWebhookPath(JsonObject? workflow)
    {
        if (workflow?["nodes"] is not JsonArray nodes)
        {
            return null;
        }

        foreach (var node in nodes.OfType<JsonObject>())
        {
            if (node["parameters"] is JsonObject parameters && parameters["path"]?.GetValue<string>() is { } path)
            {
                return path;
            }
        }

        return null;
    }

    private static string? FindExecuteWorkflowReference(JsonElement root)
    {
        if (!root.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var node in nodes.EnumerateArray())
        {
            var type = ReadString(node, "type") ?? string.Empty;
            if (type.Contains("executeWorkflow", StringComparison.OrdinalIgnoreCase)
                && node.TryGetProperty("parameters", out var parameters)
                && parameters.ValueKind == JsonValueKind.Object)
            {
                return ReadString(parameters, "workflowId") ?? ReadString(parameters, "workflowName") ?? ReadString(node, "name");
            }
        }

        return null;
    }

    private static bool Matches(EnvironmentCredentialSnapshot credential, CredentialScanItem reference) =>
        string.Equals(credential.CredentialType, reference.CredentialType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(credential.CredentialId ?? string.Empty, reference.CredentialId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && string.Equals(credential.CredentialName ?? string.Empty, reference.CredentialName ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadString(JsonObject? obj, string propertyName) =>
        obj?[propertyName]?.GetValue<string>();

    private static bool TryGetBool(JsonObject obj, string propertyName, out bool value)
    {
        if (obj[propertyName] is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out value))
        {
            return true;
        }

        value = false;
        return false;
    }
}
