using System.Text.Json;
using Application.Contracts;
using Application.Models;
using Domain;

namespace Infrastructure;

public sealed class RestoreAuditService : IRestoreAuditService
{
    private readonly AppDbContext _dbContext;

    public RestoreAuditService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task RecordAsync(RestoreAuditCreate audit, CancellationToken cancellationToken)
    {
        _dbContext.RestoreAuditLogs.Add(new RestoreAuditLog
        {
            Id = Guid.NewGuid(),
            WorkspaceId = audit.WorkspaceId,
            EnvironmentId = audit.EnvironmentId,
            EnvironmentKey = audit.EnvironmentKey,
            RestoreType = audit.RestoreType,
            SourceCommitSha = audit.SourceCommitSha,
            NewCommitSha = audit.NewCommitSha,
            FilePath = audit.FilePath,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = audit.Status,
            Warnings = JsonSerializer.Serialize(audit.Warnings),
            Errors = JsonSerializer.Serialize(audit.Errors)
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
