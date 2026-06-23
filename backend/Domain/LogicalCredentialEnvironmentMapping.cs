namespace Domain;

public sealed class LogicalCredentialEnvironmentMapping
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid LogicalCredentialId { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid EnvironmentCredentialId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
