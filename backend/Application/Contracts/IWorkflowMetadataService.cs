using Application.Models;

namespace Application.Contracts;

public interface IWorkflowMetadataService
{
    Task UpsertAsync(WorkflowMetadataUpdate update, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkflowListItemDto>> ListAsync(string environmentKey, CancellationToken cancellationToken);
}
