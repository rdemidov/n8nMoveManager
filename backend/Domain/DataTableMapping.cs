namespace Domain;

/// <summary>Maps an environment-specific n8n Data Table id to its counterpart in another environment.</summary>
public sealed class DataTableMapping
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SourceEnvironmentId { get; set; }
    public Guid TargetEnvironmentId { get; set; }
    public string SourceTableId { get; set; } = string.Empty;
    public string TargetTableId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
