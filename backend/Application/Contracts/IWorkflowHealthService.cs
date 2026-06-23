using Application.Models;
namespace Application.Contracts;
public interface IWorkflowHealthService { Task<WorkflowHealthResult> GetAsync(string environmentKey, CancellationToken cancellationToken); }
