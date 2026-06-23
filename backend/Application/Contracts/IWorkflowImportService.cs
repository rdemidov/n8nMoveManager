using Application.Models;

namespace Application.Contracts;

public interface IWorkflowImportService
{
    Task<UploadResultDto> ImportAsync(string environmentKey, IReadOnlyCollection<WorkflowUploadSource> sources, string? commitMessage, CancellationToken cancellationToken);
}
