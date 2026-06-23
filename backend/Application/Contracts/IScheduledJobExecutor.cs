namespace Application.Contracts;

public interface IScheduledJobExecutor
{
    Task RunAsync(Guid scheduledJobId, Guid? runId = null, CancellationToken cancellationToken = default);
}
