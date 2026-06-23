using Application.Models;

namespace Application.Contracts;

public interface IEnvironmentService
{
    Task<IReadOnlyList<EnvironmentDto>> ListAsync(CancellationToken cancellationToken);
    Task<EnvironmentContext> GetByKeyAsync(string environmentKey, CancellationToken cancellationToken);
    Task<EnvironmentDto> CreateAsync(EnvironmentRequest request, CancellationToken cancellationToken);
    Task<EnvironmentDto> UpdateAsync(string environmentKey, EnvironmentRequest request, CancellationToken cancellationToken);
    Task<EnvironmentClearResult> ClearAsync(string environmentKey, string? commitMessage, CancellationToken cancellationToken);
    Task<EnvironmentDeleteResult> DeleteAsync(string environmentKey, bool force, CancellationToken cancellationToken);
}
