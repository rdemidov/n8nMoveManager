namespace Domain;

/// <summary>Safe, schema-only representation of a remote n8n Data Table.</summary>
public sealed class DataTableSnapshot
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string EnvironmentKey { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ColumnsJson { get; set; } = "[]";
    public int? RowCount { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
}
