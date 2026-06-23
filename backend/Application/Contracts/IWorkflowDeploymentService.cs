using Application.Models;

namespace Application.Contracts;

public interface IWorkflowDeploymentService
{
    Task<WorkflowDeploymentPreview> PreviewAsync(WorkflowDeploymentPreviewRequest request, CancellationToken cancellationToken);
    Task<WorkflowDeploymentResult> DeployAsync(WorkflowDeploymentApplyRequest request, CancellationToken cancellationToken);
}
