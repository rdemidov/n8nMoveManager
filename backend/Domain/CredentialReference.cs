namespace Domain;

public sealed class CredentialReference
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string EnvironmentKey { get; set; } = string.Empty;
    public string? WorkflowExternalId { get; set; }
    public string WorkflowName { get; set; } = string.Empty;
    public string WorkflowFilePath { get; set; } = string.Empty;
    public string? NodeId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public string CredentialType { get; set; } = string.Empty;
    public string? CredentialId { get; set; }
    public string? CredentialName { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
}
