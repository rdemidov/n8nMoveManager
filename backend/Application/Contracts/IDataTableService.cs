using Application.Models;

namespace Application.Contracts;

public interface IDataTableService
{
    Task<PagedResult<DataTableListItemDto>> ListAsync(string environmentKey, int page, int pageSize, string? search, string? sort, string? direction, CancellationToken cancellationToken);
    Task<DataTableSyncResult> SyncAsync(string environmentKey, CancellationToken cancellationToken);
    Task<DataTableComparisonDto> CompareAsync(string sourceEnvironmentKey, string targetEnvironmentKey, CancellationToken cancellationToken);
    Task<DataTablePromotionPlan> GetPromotionPlanAsync(string sourceEnvironmentKey, string targetEnvironmentKey, CancellationToken cancellationToken);
    Task<DataTablePromotionApplyResult> ApplyPromotionAsync(DataTablePromotionApplyRequest request, CancellationToken cancellationToken);
    Task<DataTableLiveDeployResult> DeploySchemasAsync(DataTableLiveDeployRequest request, CancellationToken cancellationToken);
}
