using Application.Models;

namespace Application.Contracts;

public interface IEnvironmentDockerConfigStore
{
    Task<EnvironmentDockerConfigDto> GetAsync(string environmentKey, CancellationToken cancellationToken);
    Task<EnvironmentDockerConfigDto> SaveAsync(string environmentKey, EnvironmentDockerConfigRequest request, CancellationToken cancellationToken);
}
