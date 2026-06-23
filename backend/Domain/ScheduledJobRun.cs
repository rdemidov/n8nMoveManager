namespace Domain;

public sealed class ScheduledJobRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScheduledJobId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "queued";
    public string Logs { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string? CommitSha { get; set; }
    public string? ResultJson { get; set; }
}
