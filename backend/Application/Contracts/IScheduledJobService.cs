using Application.Models;

namespace Application.Contracts;

public interface IScheduledJobService
{
    Task<IReadOnlyList<ScheduledJobDto>> ListAsync(CancellationToken cancellationToken);
    Task<ScheduledJobDto> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ScheduledJobDto> CreateAsync(ScheduledJobRequest request, CancellationToken cancellationToken);
    Task<ScheduledJobDto> UpdateAsync(Guid id, ScheduledJobRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
    Task<ScheduledJobDto> EnableAsync(Guid id, CancellationToken cancellationToken);
    Task<ScheduledJobDto> DisableAsync(Guid id, CancellationToken cancellationToken);
    Task<ScheduledJobRunNowResult> RunNowAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScheduledJobRunSummaryDto>> ListRunsAsync(Guid id, CancellationToken cancellationToken);
    Task<ScheduledJobRunDto> GetRunAsync(Guid id, Guid runId, CancellationToken cancellationToken);
    Task RegisterEnabledJobsAsync(CancellationToken cancellationToken);
}
