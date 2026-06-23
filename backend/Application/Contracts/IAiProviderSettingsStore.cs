using Application.Models;

namespace Application.Contracts;

public interface IAiProviderSettingsStore
{
    Task<AiSettingsDto> GetAsync(CancellationToken cancellationToken);
    Task<AiProviderConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken);
    Task<AiSettingsDto> SaveAsync(AiSettingsRequest request, CancellationToken cancellationToken);
}

public sealed record AiProviderConfiguration(
    bool Enabled,
    string Endpoint,
    string ModelName,
    string ApiKey);
