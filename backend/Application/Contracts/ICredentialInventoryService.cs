using Application.Models;

namespace Application.Contracts;

public interface ICredentialInventoryService
{
    Task ReplaceWorkflowReferencesAsync(
        Guid workspaceId,
        Guid environmentId,
        string environmentKey,
        string workflowFilePath,
        IReadOnlyCollection<CredentialScanItem> references,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EnvironmentCredentialDto>> ListEnvironmentCredentialsAsync(string environmentKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<CredentialReferenceDto>> ListCredentialReferencesAsync(string environmentKey, CancellationToken cancellationToken);
}
