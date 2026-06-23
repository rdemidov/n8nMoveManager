namespace Domain;

public sealed class WorkflowMetadata
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string EnvironmentKey { get; set; } = string.Empty;
    public string? ExternalId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Active { get; set; }
    public int NodesCount { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public DateTimeOffset LastImportedAt { get; set; }
}
