namespace Infrastructure;

public interface IScheduledJobClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class ScheduledJobClock : IScheduledJobClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
