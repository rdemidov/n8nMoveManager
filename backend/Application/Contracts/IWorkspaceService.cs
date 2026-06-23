using Domain;

namespace Application.Contracts;

public interface IWorkspaceService
{
    Task<Workspace> GetOrCreateDefaultWorkspaceAsync(CancellationToken cancellationToken);
}
