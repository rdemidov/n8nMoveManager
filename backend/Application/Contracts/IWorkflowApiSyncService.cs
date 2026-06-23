using Application.Models;

namespace Application.Contracts;

public interface IWorkflowApiSyncService
{
    Task<WorkflowApiSyncResult> SyncAsync(string environmentKey, CancellationToken cancellationToken);
    Task<WorkflowApiReconciliationPreview> PreviewAsync(string environmentKey, CancellationToken cancellationToken);
    Task<WorkflowApiSyncResult> SyncSelectedAsync(string environmentKey, IReadOnlyCollection<string> workflowIds, CancellationToken cancellationToken);
}
