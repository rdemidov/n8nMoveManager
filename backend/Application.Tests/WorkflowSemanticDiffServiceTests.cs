using Application.Models;
using Xunit;

namespace Application.Tests;

public sealed class WorkflowSemanticDiffServiceTests
{
    private readonly WorkflowSemanticDiffService _service = new();

    [Fact]
    public void CompareWorkflowContent_DetectsAddedNode()
    {
        var oldWorkflow = Workflow("""[]""");
        var newWorkflow = Workflow("""[{ "id": "1", "name": "HTTP", "type": "n8n-nodes-base.httpRequest", "parameters": {} }]""");

        var diff = _service.CompareWorkflowContent(oldWorkflow, newWorkflow);

        Assert.Equal(1, diff.Summary.AddedNodes);
        Assert.Contains(diff.NodeChanges, node => node.NodeName == "HTTP" && node.ChangeType == "added");
    }

    [Fact]
    public void CompareWorkflowContent_DetectsRemovedNode()
    {
        var oldWorkflow = Workflow("""[{ "id": "1", "name": "HTTP", "type": "n8n-nodes-base.httpRequest", "parameters": {} }]""");
        var newWorkflow = Workflow("""[]""");

        var diff = _service.CompareWorkflowContent(oldWorkflow, newWorkflow);

        Assert.Equal(1, diff.Summary.RemovedNodes);
        Assert.Contains(diff.NodeChanges, node => node.NodeName == "HTTP" && node.ChangeType == "removed");
    }

    [Fact]
    public void CompareWorkflowContent_DetectsRecursiveParameterChanges()
    {
        var oldWorkflow = Workflow("""[{ "id": "1", "name": "HTTP", "type": "n8n-nodes-base.httpRequest", "parameters": { "method": "GET", "body": { "SeminarID": 10 } } }]""");
        var newWorkflow = Workflow("""[{ "id": "1", "name": "HTTP", "type": "n8n-nodes-base.httpRequest", "parameters": { "method": "POST", "body": { "SeminarID": 20 } } }]""");

        var diff = _service.CompareWorkflowContent(oldWorkflow, newWorkflow);
        var node = Assert.Single(diff.NodeChanges, node => node.ChangeType == "modified");

        Assert.Contains(node.ParameterChanges, change => change.Path == "method" && change.OldValuePreview == "GET" && change.NewValuePreview == "POST");
        Assert.Contains(node.ParameterChanges, change => change.Path == "body.SeminarID" && change.OldValuePreview == "10" && change.NewValuePreview == "20");
    }

    [Fact]
    public void CompareWorkflowContent_KeepsFullParameterValuesForHints()
    {
        var oldMessage = new string('a', 220);
        var newMessage = new string('b', 220);
        var oldWorkflow = Workflow($$"""[{ "id": "1", "name": "Agent", "type": "n8n-nodes-langchain.agent", "parameters": { "systemMessage": "{{oldMessage}}" } }]""");
        var newWorkflow = Workflow($$"""[{ "id": "1", "name": "Agent", "type": "n8n-nodes-langchain.agent", "parameters": { "systemMessage": "{{newMessage}}" } }]""");

        var diff = _service.CompareWorkflowContent(oldWorkflow, newWorkflow);
        var node = Assert.Single(diff.NodeChanges, node => node.ChangeType == "modified");
        var change = Assert.Single(node.ParameterChanges);

        Assert.Equal("systemMessage", change.Path);
        Assert.EndsWith("...", change.OldValuePreview);
        Assert.EndsWith("...", change.NewValuePreview);
        Assert.Equal(oldMessage, change.OldValueFull);
        Assert.Equal(newMessage, change.NewValueFull);
    }


    [Fact]
    public void CompareWorkflowContent_DetectsCredentialReferenceChanges()
    {
        var oldWorkflow = Workflow("""[{ "id": "1", "name": "HTTP", "type": "n8n-nodes-base.httpRequest", "credentials": { "httpBasicAuth": { "id": "old-id", "name": "Old", "type": "httpBasicAuth" } }, "parameters": {} }]""");
        var newWorkflow = Workflow("""[{ "id": "1", "name": "HTTP", "type": "n8n-nodes-base.httpRequest", "credentials": { "httpBasicAuth": { "id": "new-id", "name": "New", "type": "httpBasicAuth" } }, "parameters": {} }]""");

        var diff = _service.CompareWorkflowContent(oldWorkflow, newWorkflow);
        var credential = Assert.Single(diff.CredentialChanges);

        Assert.Equal("httpBasicAuth", credential.CredentialKey);
        Assert.Equal("old-id", credential.OldCredentialId);
        Assert.Equal("new-id", credential.NewCredentialId);
        Assert.Equal(1, diff.Summary.ChangedCredentials);
    }

    [Fact]
    public void CompareWorkflowContent_DetectsConnectionChanges()
    {
        var oldWorkflow = Workflow(
            """[{ "id": "1", "name": "Start", "type": "start" }, { "id": "2", "name": "A", "type": "set" }]""",
            "\"connections\": { \"Start\": { \"main\": [[{ \"node\": \"A\", \"type\": \"main\", \"index\": 0 }]] } }");
        var newWorkflow = Workflow(
            """[{ "id": "1", "name": "Start", "type": "start" }, { "id": "3", "name": "B", "type": "set" }]""",
            "\"connections\": { \"Start\": { \"main\": [[{ \"node\": \"B\", \"type\": \"main\", \"index\": 0 }]] } }");

        var diff = _service.CompareWorkflowContent(oldWorkflow, newWorkflow);

        Assert.Equal(2, diff.Summary.ChangedConnections);
        Assert.Contains(diff.ConnectionChanges, connection => connection.TargetNodeName == "A" && connection.ChangeType == "removed");
        Assert.Contains(diff.ConnectionChanges, connection => connection.TargetNodeName == "B" && connection.ChangeType == "added");
    }

    [Fact]
    public void CompareWorkflowContent_DetectsWorkflowSettingChanges()
    {
        var oldWorkflow = Workflow("""[]""", "\"active\": false, \"settings\": { \"timezone\": \"UTC\" }");
        var newWorkflow = Workflow("""[]""", "\"active\": true, \"settings\": { \"timezone\": \"Europe/Kiev\" }");

        var diff = _service.CompareWorkflowContent(oldWorkflow, newWorkflow);

        Assert.Equal(2, diff.Summary.ChangedWorkflowSettings);
        Assert.Contains(diff.WorkflowSettingsChanges, change => change.Path == "active");
        Assert.Contains(diff.WorkflowSettingsChanges, change => change.Path == "settings.timezone");
    }

    private static string Workflow(string nodes, string extra = "")
    {
        var extraSegment = string.IsNullOrWhiteSpace(extra) ? string.Empty : $", {extra}";
        return $$"""
        {
          "id": "workflow-1",
          "name": "Workflow",
          "nodes": {{nodes}}{{extraSegment}}
        }
        """;
    }
}
