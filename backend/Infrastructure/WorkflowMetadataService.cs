using Application.Contracts;
using Application.Models;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public sealed class WorkflowMetadataService : IWorkflowMetadataService
{
    private readonly AppDbContext _dbContext;

    public WorkflowMetadataService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task UpsertAsync(WorkflowMetadataUpdate update, CancellationToken cancellationToken)
    {
        var workflow = await _dbContext.Workflows
            .SingleOrDefaultAsync(item =>
                item.WorkspaceId == update.WorkspaceId
                && item.EnvironmentKey == update.EnvironmentKey
                && item.FilePath == update.FilePath,
                cancellationToken);

        if (workflow is null)
        {
            workflow = new WorkflowMetadata
            {
                Id = Guid.NewGuid(),
                WorkspaceId = update.WorkspaceId,
                EnvironmentId = update.EnvironmentId,
                EnvironmentKey = update.EnvironmentKey,
                FilePath = update.FilePath
            };
            _dbContext.Workflows.Add(workflow);
        }

        workflow.EnvironmentId = update.EnvironmentId;
        workflow.EnvironmentKey = update.EnvironmentKey;
        workflow.ExternalId = update.ExternalId;
        workflow.Name = update.Name;
        workflow.Active = update.Active;
        workflow.NodesCount = update.NodesCount;
        workflow.CreatedAt = update.CreatedAt;
        workflow.UpdatedAt = update.UpdatedAt;
        workflow.LastImportedAt = update.LastImportedAt;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowListItemDto>> ListAsync(string environmentKey, CancellationToken cancellationToken)
    {
        var normalizedKey = environmentKey.Trim().ToLowerInvariant();
        return await _dbContext.Workflows
            .AsNoTracking()
            .Where(workflow => workflow.EnvironmentKey == normalizedKey)
            .OrderBy(workflow => workflow.Name)
            .Select(workflow => new WorkflowListItemDto(
                workflow.ExternalId,
                workflow.Name,
                workflow.Active,
                workflow.NodesCount,
                workflow.CreatedAt,
                workflow.UpdatedAt,
                workflow.EnvironmentKey,
                workflow.FilePath,
                workflow.LastImportedAt))
            .ToArrayAsync(cancellationToken);
    }
}
