namespace Domain;

public sealed class RestoreAuditLog
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string EnvironmentKey { get; set; } = string.Empty;
    public string RestoreType { get; set; } = string.Empty;
    public string SourceCommitSha { get; set; } = string.Empty;
    public string? NewCommitSha { get; set; }
    public string? FilePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Warnings { get; set; }
    public string? Errors { get; set; }
}
