using System.Text.Json;
using Application.Models;

namespace Application;

public sealed class WorkflowCredentialScanner
{
    public IReadOnlyList<CredentialScanItem> Scan(
        JsonElement workflow,
        string workflowFilePath,
        string? workflowExternalId,
        string workflowName)
    {
        if (!workflow.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var references = new List<CredentialScanItem>();
        foreach (var node in nodes.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
        {
            if (!node.TryGetProperty("credentials", out var credentials) || credentials.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var nodeId = GetString(node, "id");
            var nodeName = GetString(node, "name") ?? "(unnamed node)";
            var nodeType = GetString(node, "type") ?? string.Empty;

            foreach (var credentialProperty in credentials.EnumerateObject())
            {
                foreach (var credential in ExpandCredentialValues(credentialProperty.Value))
                {
                    var credentialId = GetString(credential, "id");
                    var credentialName = GetString(credential, "name");
                    var credentialType = GetString(credential, "type") ?? credentialProperty.Name;

                    if (string.IsNullOrWhiteSpace(credentialId) && string.IsNullOrWhiteSpace(credentialName))
                    {
                        continue;
                    }

                    references.Add(new CredentialScanItem(
                        workflowExternalId,
                        workflowName,
                        workflowFilePath,
                        nodeId,
                        nodeName,
                        nodeType,
                        credentialType,
                        credentialId,
                        credentialName));
                }
            }
        }

        return references;
    }

    private static IEnumerable<JsonElement> ExpandCredentialValues(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => [value],
            JsonValueKind.Array => value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object),
            _ => []
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
