namespace Domain;

public sealed class LogicalCredential
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
