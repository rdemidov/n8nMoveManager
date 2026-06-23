using Application.Models;

namespace Application.Contracts;

public interface IEnvironmentN8nApiConfigStore
{
    Task<EnvironmentN8nApiConfigDto> GetAsync(string environmentKey, CancellationToken cancellationToken);
    Task<EnvironmentN8nApiConfigDto> SaveAsync(string environmentKey, EnvironmentN8nApiConfigRequest request, CancellationToken cancellationToken);
    Task<string?> GetApiKeyAsync(string environmentKey, CancellationToken cancellationToken);
}
