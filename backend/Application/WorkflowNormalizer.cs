using System.Text.Json;
using System.Text.Json.Nodes;

namespace Application;

public sealed class WorkflowNormalizer
{
    private static readonly HashSet<string> VolatileFields = new(StringComparer.Ordinal)
    {
        "createdAt",
        "updatedAt",
        "versionId",
        "staticData",
        "pinData"
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public string Normalize(JsonElement workflow)
    {
        if (workflow.ValueKind != JsonValueKind.Object)
        {
            throw new WorkflowImportException("Each workflow must be a JSON object.");
        }

        var node = JsonNode.Parse(workflow.GetRawText()) as JsonObject
            ?? throw new WorkflowImportException("Workflow JSON could not be parsed.");

        foreach (var field in VolatileFields)
        {
            node.Remove(field);
        }

        var sorted = new JsonObject();
        foreach (var property in node.OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            sorted[property.Key] = property.Value?.DeepClone();
        }

        return sorted.ToJsonString(WriteOptions) + Environment.NewLine;
    }
}
