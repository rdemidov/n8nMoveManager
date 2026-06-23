using Application.Models;

namespace Application.Contracts;

public interface IPromotionAuditService
{
    Task RecordAsync(PromotionAuditCreate audit, CancellationToken cancellationToken);
    Task<IReadOnlyList<AppliedManualMergeAuditEntry>> ListAppliedManualMergesAsync(Guid workspaceId, Guid sourceEnvironmentId, Guid targetEnvironmentId, CancellationToken cancellationToken);
}
