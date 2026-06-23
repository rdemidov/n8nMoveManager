using Application.Contracts;
using Application.Models;
using Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Infrastructure;

public sealed class PromotionAuditService : IPromotionAuditService
{
    private readonly AppDbContext _dbContext;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PromotionAuditService(AppDbContext dbContext, IHttpContextAccessor httpContextAccessor)
    {
        _dbContext = dbContext;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task RecordAsync(PromotionAuditCreate audit, CancellationToken cancellationToken)
    {
        _dbContext.PromotionAuditLogs.Add(new PromotionAuditLog
        {
            Id = Guid.NewGuid(),
            WorkspaceId = audit.WorkspaceId,
            SourceEnvironmentId = audit.SourceEnvironmentId,
            SourceEnvironmentKey = audit.SourceEnvironmentKey,
            TargetEnvironmentId = audit.TargetEnvironmentId,
            TargetEnvironmentKey = audit.TargetEnvironmentKey,
            Status = audit.Status,
            CreatedAt = DateTimeOffset.UtcNow,
            AppliedAt = audit.AppliedAt,
            CommitSha = audit.CommitSha,
            Summary = audit.Summary
            ,ActorUserName = _httpContextAccessor.HttpContext?.User.Identity?.Name
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AppliedManualMergeAuditEntry>> ListAppliedManualMergesAsync(Guid workspaceId, Guid sourceEnvironmentId, Guid targetEnvironmentId, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.PromotionAuditLogs
            .AsNoTracking()
            .Where(log => log.WorkspaceId == workspaceId
                && log.SourceEnvironmentId == sourceEnvironmentId
                && log.TargetEnvironmentId == targetEnvironmentId
                && log.Status == "manual-merge-applied")
            .Select(log => new { log.Summary, log.CommitSha, AppliedAt = log.AppliedAt ?? log.CreatedAt })
            .ToArrayAsync(cancellationToken);

        var entries = new Dictionary<string, AppliedManualMergeAuditEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.OrderByDescending(row => row.AppliedAt))
        {
            var path = ReadWorkflowFilePath(row.Summary);
            if (!string.IsNullOrWhiteSpace(path) && !entries.ContainsKey(path))
            {
                entries[path] = new AppliedManualMergeAuditEntry(path, row.CommitSha, row.AppliedAt);
            }
        }

        return entries.Values.ToArray();
    }

    private static string? ReadWorkflowFilePath(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(summary);
            return document.RootElement.TryGetProperty("WorkflowFilePath", out var path)
                ? path.GetString()
                : document.RootElement.TryGetProperty("workflowFilePath", out path) ? path.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
