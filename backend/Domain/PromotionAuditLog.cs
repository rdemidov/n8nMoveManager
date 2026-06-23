namespace Domain;

public sealed class PromotionAuditLog
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SourceEnvironmentId { get; set; }
    public string SourceEnvironmentKey { get; set; } = string.Empty;
    public Guid TargetEnvironmentId { get; set; }
    public string TargetEnvironmentKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public string? CommitSha { get; set; }
    public string? Summary { get; set; }
    public string? ActorUserName { get; set; }
}
