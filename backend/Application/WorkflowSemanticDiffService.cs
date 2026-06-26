using System.Text.Json;
using Application.Models;

namespace Application;

public sealed class WorkflowSemanticDiffService
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = true };
    private static readonly HashSet<string> WorkflowSettingFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "active",
        "settings",
        "triggerCount",
        "tags"
    };

    public WorkflowSemanticDiffCollectionDto CompareWorkflowFiles(
        IReadOnlyDictionary<string, string> oldFiles,
        IReadOnlyDictionary<string, string> newFiles,
        string? source,
        string? target)
    {
        var paths = oldFiles.Keys
            .Concat(newFiles.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        var workflows = paths
            .Select(path =>
            {
                oldFiles.TryGetValue(path, out var oldContent);
                newFiles.TryGetValue(path, out var newContent);
                return CompareWorkflowContent(oldContent, newContent, oldContent is null ? null : path, newContent is null ? null : path);
            })
            .ToArray();

        return new WorkflowSemanticDiffCollectionDto(source, target, DateTimeOffset.UtcNow, workflows);
    }

    public WorkflowSemanticDiffDto CompareWorkflowContent(string? oldContent, string? newContent, string? oldFilePath = null, string? newFilePath = null)
    {
        using var oldDocument = TryParse(oldContent, oldFilePath, out var oldWarning);
        using var newDocument = TryParse(newContent, newFilePath, out var newWarning);
        var warnings = new[] { oldWarning, newWarning }.Where(warning => !string.IsNullOrWhiteSpace(warning)).Cast<string>().ToList();
        var oldWorkflow = oldDocument?.RootElement;
        var newWorkflow = newDocument?.RootElement;

        if (oldWorkflow is null && newWorkflow is null)
        {
            return Empty(Path.GetFileNameWithoutExtension(newFilePath ?? oldFilePath ?? "Invalid workflow"), "unchanged", oldFilePath, newFilePath, warnings);
        }

        if (oldWorkflow is null)
        {
            return BuildSingleSided(newWorkflow!.Value, "added", oldFilePath, newFilePath, warnings);
        }

        if (newWorkflow is null)
        {
            return BuildSingleSided(oldWorkflow.Value, "removed", oldFilePath, newFilePath, warnings);
        }

        var nodeChanges = CompareNodes(oldWorkflow.Value, newWorkflow.Value);
        var connectionChanges = CompareConnections(oldWorkflow.Value, newWorkflow.Value);
        var workflowSettingsChanges = CompareWorkflowSettings(oldWorkflow.Value, newWorkflow.Value);
        var credentialChanges = nodeChanges.SelectMany(node => node.CredentialChanges).ToArray();
        var summary = new WorkflowSemanticDiffSummaryDto(
            nodeChanges.Count(node => node.ChangeType == "added"),
            nodeChanges.Count(node => node.ChangeType == "removed"),
            nodeChanges.Count(node => node.ChangeType == "modified"),
            nodeChanges.Count(node => node.ChangeType == "unchanged"),
            connectionChanges.Count,
            credentialChanges.Length,
            workflowSettingsChanges.Count);
        var changeType = summary.AddedNodes == 0
            && summary.RemovedNodes == 0
            && summary.ModifiedNodes == 0
            && summary.ChangedConnections == 0
            && summary.ChangedCredentials == 0
            && summary.ChangedWorkflowSettings == 0
                ? "unchanged"
                : "modified";

        return new WorkflowSemanticDiffDto(
            ReadString(newWorkflow.Value, "id") ?? ReadString(oldWorkflow.Value, "id"),
            ReadString(newWorkflow.Value, "name") ?? ReadString(oldWorkflow.Value, "name") ?? Path.GetFileNameWithoutExtension(newFilePath ?? oldFilePath ?? "Workflow"),
            changeType,
            summary,
            nodeChanges,
            connectionChanges,
            credentialChanges,
            workflowSettingsChanges,
            oldFilePath,
            newFilePath,
            warnings);
    }

    private static WorkflowSemanticDiffDto BuildSingleSided(JsonElement workflow, string changeType, string? oldFilePath, string? newFilePath, IReadOnlyList<string> warnings)
    {
        var nodes = ReadNodes(workflow)
            .Select(node => new NodeSemanticDiffDto(
                ReadString(node, "id"),
                ReadString(node, "name") ?? "Unnamed node",
                ReadString(node, "type") ?? "unknown",
                changeType,
                [],
                ReadCredentials(node, ReadString(node, "name") ?? "Unnamed node")
                    .Select(credential => new CredentialSemanticDiffDto(
                        credential.NodeName,
                        credential.Key,
                        credential.Type,
                        changeType == "removed" ? credential.Id : null,
                        changeType == "removed" ? credential.Name : null,
                        changeType == "added" ? credential.Id : null,
                        changeType == "added" ? credential.Name : null))
                    .ToArray(),
                []))
            .ToArray();
        var credentialChanges = nodes.SelectMany(node => node.CredentialChanges).ToArray();
        var connections = changeType == "unchanged" ? [] : ReadConnections(workflow)
            .Select(connection => new ConnectionSemanticDiffDto(connection.SourceNodeName, connection.TargetNodeName, connection.OutputIndex, connection.InputIndex, changeType))
            .ToArray();
        var settings = WorkflowSettingFields
            .Where(field => workflow.TryGetProperty(field, out _))
            .Select(field => new ParameterSemanticDiffDto(
                field,
                changeType == "removed" ? Preview(workflow.GetProperty(field)) : null,
                changeType == "added" ? Preview(workflow.GetProperty(field)) : null,
                ValueType(workflow.GetProperty(field)),
                "normal",
                changeType == "removed" ? FullValue(workflow.GetProperty(field)) : null,
                changeType == "added" ? FullValue(workflow.GetProperty(field)) : null))
            .ToArray();
        var summary = new WorkflowSemanticDiffSummaryDto(
            changeType == "added" ? nodes.Length : 0,
            changeType == "removed" ? nodes.Length : 0,
            0,
            0,
            connections.Length,
            credentialChanges.Length,
            settings.Length);

        return new WorkflowSemanticDiffDto(
            ReadString(workflow, "id"),
            ReadString(workflow, "name") ?? Path.GetFileNameWithoutExtension(newFilePath ?? oldFilePath ?? "Workflow"),
            changeType,
            summary,
            nodes,
            connections,
            credentialChanges,
            settings,
            oldFilePath,
            newFilePath,
            warnings);
    }

    private static WorkflowSemanticDiffDto Empty(string workflowName, string changeType, string? oldFilePath, string? newFilePath, IReadOnlyList<string> warnings)
    {
        return new WorkflowSemanticDiffDto(null, workflowName, changeType, new WorkflowSemanticDiffSummaryDto(0, 0, 0, 0, 0, 0, 0), [], [], [], [], oldFilePath, newFilePath, warnings);
    }

    private static IReadOnlyList<NodeSemanticDiffDto> CompareNodes(JsonElement oldWorkflow, JsonElement newWorkflow)
    {
        var oldNodes = ReadNodes(oldWorkflow);
        var newNodes = ReadNodes(newWorkflow);
        var matchedOld = new HashSet<int>();
        var matchedNew = new HashSet<int>();
        var changes = new List<NodeSemanticDiffDto>();

        for (var newIndex = 0; newIndex < newNodes.Count; newIndex++)
        {
            var match = FindNodeMatch(newNodes[newIndex], oldNodes, matchedOld);
            if (match < 0)
            {
                continue;
            }

            matchedOld.Add(match);
            matchedNew.Add(newIndex);
            changes.Add(CompareNode(oldNodes[match], newNodes[newIndex]));
        }

        for (var oldIndex = 0; oldIndex < oldNodes.Count; oldIndex++)
        {
            if (!matchedOld.Contains(oldIndex))
            {
                changes.Add(SingleNode(oldNodes[oldIndex], "removed"));
            }
        }

        for (var newIndex = 0; newIndex < newNodes.Count; newIndex++)
        {
            if (!matchedNew.Contains(newIndex))
            {
                changes.Add(SingleNode(newNodes[newIndex], "added"));
            }
        }

        return changes
            .OrderBy(change => change.NodeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(change => change.NodeType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static NodeSemanticDiffDto CompareNode(JsonElement oldNode, JsonElement newNode)
    {
        var nodeName = ReadString(newNode, "name") ?? ReadString(oldNode, "name") ?? "Unnamed node";
        var nodeType = ReadString(newNode, "type") ?? ReadString(oldNode, "type") ?? "unknown";
        var parameterChanges = CompareOptionalElement(oldNode, newNode, "parameters", string.Empty, "normal");
        var credentialChanges = CompareCredentials(oldNode, newNode, nodeName);
        var metadataChanges = new List<ParameterSemanticDiffDto>();

        AddFieldChange(oldNode, newNode, "position", "position", "low", metadataChanges);
        AddFieldChange(oldNode, newNode, "disabled", "disabled", "normal", metadataChanges);
        AddFieldChange(oldNode, newNode, "active", "active", "normal", metadataChanges);

        var changeType = parameterChanges.Count == 0 && credentialChanges.Count == 0 && metadataChanges.Count == 0
            ? "unchanged"
            : "modified";

        return new NodeSemanticDiffDto(
            ReadString(newNode, "id") ?? ReadString(oldNode, "id"),
            nodeName,
            nodeType,
            changeType,
            parameterChanges,
            credentialChanges,
            metadataChanges);
    }

    private static NodeSemanticDiffDto SingleNode(JsonElement node, string changeType)
    {
        var nodeName = ReadString(node, "name") ?? "Unnamed node";
        return new NodeSemanticDiffDto(
            ReadString(node, "id"),
            nodeName,
            ReadString(node, "type") ?? "unknown",
            changeType,
            [],
            ReadCredentials(node, nodeName)
                .Select(credential => new CredentialSemanticDiffDto(
                    credential.NodeName,
                    credential.Key,
                    credential.Type,
                    changeType == "removed" ? credential.Id : null,
                    changeType == "removed" ? credential.Name : null,
                    changeType == "added" ? credential.Id : null,
                    changeType == "added" ? credential.Name : null))
                .ToArray(),
            []);
    }

    private static int FindNodeMatch(JsonElement newNode, IReadOnlyList<JsonElement> oldNodes, HashSet<int> matchedOld)
    {
        var newId = ReadString(newNode, "id");
        if (!string.IsNullOrWhiteSpace(newId))
        {
            for (var i = 0; i < oldNodes.Count; i++)
            {
                if (!matchedOld.Contains(i) && string.Equals(newId, ReadString(oldNodes[i], "id"), StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        var newName = ReadString(newNode, "name");
        var newType = ReadString(newNode, "type");
        if (string.IsNullOrWhiteSpace(newName) || string.IsNullOrWhiteSpace(newType))
        {
            return -1;
        }

        for (var i = 0; i < oldNodes.Count; i++)
        {
            if (!matchedOld.Contains(i)
                && string.Equals(newName, ReadString(oldNodes[i], "name"), StringComparison.Ordinal)
                && string.Equals(newType, ReadString(oldNodes[i], "type"), StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static IReadOnlyList<ParameterSemanticDiffDto> CompareWorkflowSettings(JsonElement oldWorkflow, JsonElement newWorkflow)
    {
        var changes = new List<ParameterSemanticDiffDto>();
        foreach (var field in WorkflowSettingFields)
        {
            AddFieldChange(oldWorkflow, newWorkflow, field, field, "normal", changes);
        }

        return changes;
    }

    private static IReadOnlyList<ParameterSemanticDiffDto> CompareOptionalElement(JsonElement oldParent, JsonElement newParent, string propertyName, string pathPrefix, string importance)
    {
        var oldExists = oldParent.TryGetProperty(propertyName, out var oldElement);
        var newExists = newParent.TryGetProperty(propertyName, out var newElement);
        if (!oldExists && !newExists)
        {
            return [];
        }

        return CompareElements(oldExists ? oldElement : default, newExists ? newElement : default, oldExists, newExists, pathPrefix, importance);
    }

    private static void AddFieldChange(JsonElement oldParent, JsonElement newParent, string propertyName, string path, string importance, ICollection<ParameterSemanticDiffDto> changes)
    {
        var oldExists = oldParent.TryGetProperty(propertyName, out var oldElement);
        var newExists = newParent.TryGetProperty(propertyName, out var newElement);
        if (!oldExists && !newExists)
        {
            return;
        }

        foreach (var change in CompareElements(oldElement, newElement, oldExists, newExists, path, importance))
        {
            changes.Add(change);
        }
    }

    private static IReadOnlyList<ParameterSemanticDiffDto> CompareElements(JsonElement oldElement, JsonElement newElement, bool oldExists, bool newExists, string path, string importance)
    {
        if (oldExists && newExists && JsonEqual(oldElement, newElement))
        {
            return [];
        }

        if (oldExists && newExists && oldElement.ValueKind == JsonValueKind.Object && newElement.ValueKind == JsonValueKind.Object)
        {
            return oldElement.EnumerateObject().Select(property => property.Name)
                .Concat(newElement.EnumerateObject().Select(property => property.Name))
                .Distinct(StringComparer.Ordinal)
                .SelectMany(name =>
                {
                    var oldChildExists = oldElement.TryGetProperty(name, out var oldChild);
                    var newChildExists = newElement.TryGetProperty(name, out var newChild);
                    var childPath = string.IsNullOrWhiteSpace(path) ? name : $"{path}.{name}";
                    return CompareElements(oldChild, newChild, oldChildExists, newChildExists, childPath, importance);
                })
                .ToArray();
        }

        if (oldExists && newExists && oldElement.ValueKind == JsonValueKind.Array && newElement.ValueKind == JsonValueKind.Array)
        {
            var oldItems = oldElement.EnumerateArray().ToArray();
            var newItems = newElement.EnumerateArray().ToArray();
            if (oldItems.Length == newItems.Length)
            {
                var itemChanges = new List<ParameterSemanticDiffDto>();
                for (var i = 0; i < oldItems.Length; i++)
                {
                    itemChanges.AddRange(CompareElements(oldItems[i], newItems[i], true, true, $"{path}[{i}]", importance));
                }

                if (itemChanges.Count > 0)
                {
                    return itemChanges;
                }
            }
        }

        return
        [
            new ParameterSemanticDiffDto(
                string.IsNullOrWhiteSpace(path) ? "(root)" : path,
                oldExists ? Preview(oldElement) : null,
                newExists ? Preview(newElement) : null,
                newExists ? ValueType(newElement) : oldExists ? ValueType(oldElement) : "missing",
                importance,
                oldExists ? FullValue(oldElement) : null,
                newExists ? FullValue(newElement) : null)
        ];
    }

    private static IReadOnlyList<CredentialSemanticDiffDto> CompareCredentials(JsonElement oldNode, JsonElement newNode, string nodeName)
    {
        var oldCredentials = ReadCredentials(oldNode, nodeName).ToDictionary(credential => credential.Key, StringComparer.OrdinalIgnoreCase);
        var newCredentials = ReadCredentials(newNode, nodeName).ToDictionary(credential => credential.Key, StringComparer.OrdinalIgnoreCase);
        return oldCredentials.Keys
            .Concat(newCredentials.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(key => !oldCredentials.TryGetValue(key, out var oldCredential)
                || !newCredentials.TryGetValue(key, out var newCredential)
                || !string.Equals(oldCredential.Id ?? string.Empty, newCredential.Id ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(oldCredential.Name ?? string.Empty, newCredential.Name ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(oldCredential.Type, newCredential.Type, StringComparison.Ordinal))
            .Select(key =>
            {
                oldCredentials.TryGetValue(key, out var oldCredential);
                newCredentials.TryGetValue(key, out var newCredential);
                return new CredentialSemanticDiffDto(
                    nodeName,
                    key,
                    newCredential?.Type ?? oldCredential?.Type ?? key,
                    oldCredential?.Id,
                    oldCredential?.Name,
                    newCredential?.Id,
                    newCredential?.Name);
            })
            .ToArray();
    }

    private static IEnumerable<CredentialInfo> ReadCredentials(JsonElement node, string nodeName)
    {
        if (!node.TryGetProperty("credentials", out var credentials) || credentials.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in credentials.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                yield return ReadCredential(nodeName, property.Name, property.Value);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in property.Value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
                {
                    yield return ReadCredential(nodeName, $"{property.Name}[{index++}]", item, property.Name);
                }
            }
        }
    }

    private static CredentialInfo ReadCredential(string nodeName, string key, JsonElement credential, string? fallbackType = null)
    {
        return new CredentialInfo(
            nodeName,
            key,
            ReadString(credential, "type") ?? fallbackType ?? key,
            ReadString(credential, "id"),
            ReadString(credential, "name"));
    }

    private static IReadOnlyList<ConnectionSemanticDiffDto> CompareConnections(JsonElement oldWorkflow, JsonElement newWorkflow)
    {
        var oldConnections = ReadConnections(oldWorkflow).ToDictionary(connection => connection.Key, StringComparer.Ordinal);
        var newConnections = ReadConnections(newWorkflow).ToDictionary(connection => connection.Key, StringComparer.Ordinal);
        var removed = oldConnections.Keys.Except(newConnections.Keys, StringComparer.Ordinal).Select(key => oldConnections[key]).ToArray();
        var added = newConnections.Keys.Except(oldConnections.Keys, StringComparer.Ordinal).Select(key => newConnections[key]).ToArray();
        var addedPairs = added.ToLookup(connection => $"{connection.SourceNodeName}|{connection.TargetNodeName}", StringComparer.OrdinalIgnoreCase);
        var changed = new List<ConnectionSemanticDiffDto>();
        var consumedAdded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var oldConnection in removed)
        {
            var pair = $"{oldConnection.SourceNodeName}|{oldConnection.TargetNodeName}";
            var newConnection = addedPairs[pair].FirstOrDefault(connection => !consumedAdded.Contains(connection.Key));
            if (newConnection is null)
            {
                changed.Add(new ConnectionSemanticDiffDto(oldConnection.SourceNodeName, oldConnection.TargetNodeName, oldConnection.OutputIndex, oldConnection.InputIndex, "removed"));
                continue;
            }

            consumedAdded.Add(newConnection.Key);
            changed.Add(new ConnectionSemanticDiffDto(newConnection.SourceNodeName, newConnection.TargetNodeName, newConnection.OutputIndex, newConnection.InputIndex, "changed"));
        }

        changed.AddRange(added
            .Where(connection => !consumedAdded.Contains(connection.Key))
            .Select(connection => new ConnectionSemanticDiffDto(connection.SourceNodeName, connection.TargetNodeName, connection.OutputIndex, connection.InputIndex, "added")));
        return changed;
    }

    private static IReadOnlyList<ConnectionInfo> ReadConnections(JsonElement workflow)
    {
        if (!workflow.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<ConnectionInfo>();
        foreach (var source in connections.EnumerateObject())
        {
            if (source.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var group in source.Value.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var outputIndex = 0;
                foreach (var output in group.Value.EnumerateArray())
                {
                    if (output.ValueKind != JsonValueKind.Array)
                    {
                        outputIndex++;
                        continue;
                    }

                    foreach (var target in output.EnumerateArray().Where(target => target.ValueKind == JsonValueKind.Object))
                    {
                        var targetName = ReadString(target, "node");
                        if (string.IsNullOrWhiteSpace(targetName))
                        {
                            continue;
                        }

                        var inputIndex = target.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var parsed)
                            ? parsed
                            : (int?)null;
                        result.Add(new ConnectionInfo(
                            $"{source.Name}|{targetName}|{group.Name}|{outputIndex}|{inputIndex}",
                            source.Name,
                            targetName,
                            outputIndex,
                            inputIndex));
                    }

                    outputIndex++;
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<JsonElement> ReadNodes(JsonElement workflow)
    {
        if (!workflow.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return nodes.EnumerateArray().Where(node => node.ValueKind == JsonValueKind.Object).ToArray();
    }

    private static JsonDocument? TryParse(string? content, string? path, out string? warning)
    {
        warning = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(content, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                warning = $"File '{path ?? "workflow"}' is not a single n8n workflow object.";
                document.Dispose();
                return null;
            }

            if (!document.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            {
                warning = $"Workflow '{path ?? ReadString(document.RootElement, "name") ?? "workflow"}' has no nodes array.";
            }

            return document;
        }
        catch (JsonException ex)
        {
            warning = $"File '{path ?? "workflow"}' contains invalid workflow JSON: {ex.Message}";
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static bool JsonEqual(JsonElement left, JsonElement right)
    {
        return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
    }

    private static string ValueType(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => "unknown"
        };
    }

    private static string Preview(JsonElement element)
    {
        var preview = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => element.GetRawText(),
            _ => element.GetRawText()
        };

        preview = preview.ReplaceLineEndings(" ");
        return preview.Length <= 140 ? preview : $"{preview[..137]}...";
    }

    private static string FullValue(JsonElement element)
    {
        var value = element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();

        return value.ReplaceLineEndings(" ");
    }

    private sealed record CredentialInfo(string NodeName, string Key, string Type, string? Id, string? Name);

    private sealed record ConnectionInfo(string Key, string SourceNodeName, string TargetNodeName, int? OutputIndex, int? InputIndex);
}
