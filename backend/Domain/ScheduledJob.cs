namespace Domain;

public sealed class ScheduledJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public Guid EnvironmentId { get; set; }
    public string CronExpression { get; set; } = string.Empty;
    public string Timezone { get; set; } = "Europe/Kyiv";
    public bool IsEnabled { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
}
