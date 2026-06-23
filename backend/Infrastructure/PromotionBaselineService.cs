using Application;
using Application.Contracts;
using Application.Models;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public sealed class PromotionBaselineService : IPromotionBaselineService
{
    private readonly AppDbContext _dbContext;
    private readonly IEnvironmentService _environmentService;
    private readonly IGitRepositoryService _gitRepositoryService;

    public PromotionBaselineService(AppDbContext dbContext, IEnvironmentService environmentService, IGitRepositoryService gitRepositoryService)
    {
        _dbContext = dbContext;
        _environmentService = environmentService;
        _gitRepositoryService = gitRepositoryService;
    }

    public async Task<PromotionComparisonBaselineDto?> GetAsync(string sourceEnvironmentKey, string targetEnvironmentKey, CancellationToken cancellationToken)
    {
        var source = await _environmentService.GetByKeyAsync(sourceEnvironmentKey, cancellationToken);
        var target = await _environmentService.GetByKeyAsync(targetEnvironmentKey, cancellationToken);
        var baseline = await _dbContext.PromotionComparisonBaselines.AsNoTracking().SingleOrDefaultAsync(item =>
            item.WorkspaceId == source.Workspace.Id
            && item.SourceEnvironmentId == source.Environment.Id
            && item.TargetEnvironmentId == target.Environment.Id,
            cancellationToken);
        return baseline is null ? null : ToDto(baseline);
    }

    public async Task<PromotionComparisonBaselineDto?> SetAsync(PromotionComparisonBaselineRequest request, CancellationToken cancellationToken)
    {
        var source = await _environmentService.GetByKeyAsync(request.SourceEnvironmentKey, cancellationToken);
        var target = await _environmentService.GetByKeyAsync(request.TargetEnvironmentKey, cancellationToken);
        if (source.Environment.Id == target.Environment.Id)
        {
            throw new WorkflowImportException("Source and target environments must be different.");
        }

        var existing = await _dbContext.PromotionComparisonBaselines.SingleOrDefaultAsync(item =>
            item.WorkspaceId == source.Workspace.Id
            && item.SourceEnvironmentId == source.Environment.Id
            && item.TargetEnvironmentId == target.Environment.Id,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(request.CommitSha))
        {
            if (existing is not null)
            {
                _dbContext.PromotionComparisonBaselines.Remove(existing);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            return null;
        }

        var commit = _gitRepositoryService.GetCommit(source.Workspace.RepoPath, request.CommitSha.Trim())
            ?? throw new WorkflowImportException("Baseline commit was not found in this workspace repository.");
        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            existing = new PromotionComparisonBaseline
            {
                Id = Guid.NewGuid(),
                WorkspaceId = source.Workspace.Id,
                SourceEnvironmentId = source.Environment.Id,
                TargetEnvironmentId = target.Environment.Id,
                CreatedAt = now
            };
            _dbContext.PromotionComparisonBaselines.Add(existing);
        }

        existing.CommitSha = commit.Sha;
        existing.Label = string.IsNullOrWhiteSpace(request.Label) ? commit.Message : request.Label.Trim();
        existing.UpdatedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(existing);
    }

    private static PromotionComparisonBaselineDto ToDto(PromotionComparisonBaseline baseline) =>
        new(baseline.CommitSha, baseline.Label, baseline.CreatedAt, baseline.UpdatedAt);
}
