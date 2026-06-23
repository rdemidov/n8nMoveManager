namespace Domain;

public sealed class PromotionComparisonBaseline
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SourceEnvironmentId { get; set; }
    public Guid TargetEnvironmentId { get; set; }
    public string CommitSha { get; set; } = string.Empty;
    public string? Label { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
