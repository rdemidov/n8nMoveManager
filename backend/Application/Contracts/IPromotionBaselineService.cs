using Application.Models;

namespace Application.Contracts;

public interface IPromotionBaselineService
{
    Task<PromotionComparisonBaselineDto?> GetAsync(string sourceEnvironmentKey, string targetEnvironmentKey, CancellationToken cancellationToken);
    Task<PromotionComparisonBaselineDto?> SetAsync(PromotionComparisonBaselineRequest request, CancellationToken cancellationToken);
}
