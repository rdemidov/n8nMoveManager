using Application.Models;

namespace Application.Contracts;

public interface IScheduledJobScheduler
{
    void Register(ScheduledJobDto job);
    void Remove(Guid scheduledJobId);
    string EnqueueRun(Guid scheduledJobId, Guid runId);
}
