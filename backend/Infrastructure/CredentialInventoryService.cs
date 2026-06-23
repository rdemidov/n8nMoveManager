using Application.Contracts;
using Application.Models;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public sealed class CredentialInventoryService : ICredentialInventoryService
{
    private readonly AppDbContext _dbContext;
    private readonly IEnvironmentService _environmentService;

    public CredentialInventoryService(AppDbContext dbContext, IEnvironmentService environmentService)
    {
        _dbContext = dbContext;
        _environmentService = environmentService;
    }

    public async Task ReplaceWorkflowReferencesAsync(
        Guid workspaceId,
        Guid environmentId,
        string environmentKey,
        string workflowFilePath,
        IReadOnlyCollection<CredentialScanItem> references,
        CancellationToken cancellationToken)
    {
        var existing = _dbContext.CredentialReferences
            .Where(reference =>
                reference.WorkspaceId == workspaceId
                && reference.EnvironmentId == environmentId
                && reference.WorkflowFilePath == workflowFilePath);
        _dbContext.CredentialReferences.RemoveRange(existing);

        var now = DateTimeOffset.UtcNow;
        foreach (var reference in references)
        {
            _dbContext.CredentialReferences.Add(new CredentialReference
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                EnvironmentId = environmentId,
                EnvironmentKey = environmentKey,
                WorkflowExternalId = reference.WorkflowExternalId,
                WorkflowName = reference.WorkflowName,
                WorkflowFilePath = reference.WorkflowFilePath,
                NodeId = reference.NodeId,
                NodeName = reference.NodeName,
                NodeType = reference.NodeType,
                CredentialType = reference.CredentialType,
                CredentialId = reference.CredentialId,
                CredentialName = reference.CredentialName,
                DetectedAt = now
            });

            var credential = _dbContext.EnvironmentCredentials.Local.SingleOrDefault(item =>
                item.WorkspaceId == workspaceId
                && item.EnvironmentId == environmentId
                && item.CredentialType == reference.CredentialType
                && item.CredentialId == reference.CredentialId
                && item.CredentialName == reference.CredentialName)
                ?? await _dbContext.EnvironmentCredentials.SingleOrDefaultAsync(item =>
                    item.WorkspaceId == workspaceId
                    && item.EnvironmentId == environmentId
                    && item.CredentialType == reference.CredentialType
                    && item.CredentialId == reference.CredentialId
                    && item.CredentialName == reference.CredentialName,
                    cancellationToken);

            if (credential is null)
            {
                credential = new EnvironmentCredential
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    EnvironmentId = environmentId,
                    EnvironmentKey = environmentKey,
                    CredentialType = reference.CredentialType,
                    CredentialId = reference.CredentialId,
                    CredentialName = reference.CredentialName,
                    FirstDetectedAt = now
                };
                _dbContext.EnvironmentCredentials.Add(credential);
            }

            credential.EnvironmentKey = environmentKey;
            credential.LastDetectedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EnvironmentCredentialDto>> ListEnvironmentCredentialsAsync(string environmentKey, CancellationToken cancellationToken)
    {
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        var counts = await _dbContext.CredentialReferences
            .AsNoTracking()
            .Where(reference => reference.EnvironmentId == context.Environment.Id)
            .GroupBy(reference => new { reference.CredentialType, reference.CredentialId, reference.CredentialName })
            .Select(group => new
            {
                group.Key.CredentialType,
                group.Key.CredentialId,
                group.Key.CredentialName,
                Count = group.Count()
            })
            .ToArrayAsync(cancellationToken);

        var credentials = await _dbContext.EnvironmentCredentials
            .AsNoTracking()
            .Where(credential => credential.EnvironmentId == context.Environment.Id)
            .OrderBy(credential => credential.CredentialType)
            .ThenBy(credential => credential.CredentialName)
            .ToArrayAsync(cancellationToken);

        return credentials.Select(credential =>
        {
            var count = counts.FirstOrDefault(item =>
                item.CredentialType == credential.CredentialType
                && item.CredentialId == credential.CredentialId
                && item.CredentialName == credential.CredentialName)?.Count ?? 0;
            return new EnvironmentCredentialDto(
                credential.Id,
                credential.EnvironmentKey,
                credential.CredentialType,
                credential.CredentialId,
                credential.CredentialName,
                count,
                credential.LastDetectedAt);
        }).ToArray();
    }

    public async Task<IReadOnlyList<CredentialReferenceDto>> ListCredentialReferencesAsync(string environmentKey, CancellationToken cancellationToken)
    {
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        return await _dbContext.CredentialReferences
            .AsNoTracking()
            .Where(reference => reference.EnvironmentId == context.Environment.Id)
            .OrderBy(reference => reference.WorkflowName)
            .ThenBy(reference => reference.NodeName)
            .Select(reference => new CredentialReferenceDto(
                reference.Id,
                reference.EnvironmentKey,
                reference.WorkflowExternalId,
                reference.WorkflowName,
                reference.WorkflowFilePath,
                reference.NodeId,
                reference.NodeName,
                reference.NodeType,
                reference.CredentialType,
                reference.CredentialId,
                reference.CredentialName,
                reference.DetectedAt))
            .ToArrayAsync(cancellationToken);
    }
}
