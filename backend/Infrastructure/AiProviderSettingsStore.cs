using Application.Contracts;
using Application.Models;
using Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace Infrastructure;

public sealed class AiProviderSettingsStore : IAiProviderSettingsStore
{
    private const string StorageWarning = "API key is encrypted at rest with this application's data-protection key. Persist that key outside ephemeral storage in production.";
    private readonly AppDbContext _dbContext;
    private readonly IDataProtector _protector;

    public AiProviderSettingsStore(AppDbContext dbContext, IDataProtectionProvider dataProtectionProvider)
    {
        _dbContext = dbContext;
        _protector = dataProtectionProvider.CreateProtector("n8n-move-manager.ai-provider-key.v1");
    }

    public async Task<AiSettingsDto> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await GetEntityAsync(cancellationToken);
        return ToDto(settings);
    }

    public async Task<AiProviderConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        var settings = await GetEntityAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return null;
        }

        return new AiProviderConfiguration(
            settings.Enabled,
            settings.Endpoint,
            settings.ModelName,
            Unprotect(settings.SensitiveApiKey) ?? string.Empty);
    }

    public async Task<AiSettingsDto> SaveAsync(AiSettingsRequest request, CancellationToken cancellationToken)
    {
        var settings = await GetEntityAsync(cancellationToken);
        settings.Enabled = request.Enabled;
        settings.Endpoint = request.Endpoint?.Trim() ?? string.Empty;
        settings.ModelName = request.ModelName?.Trim() ?? string.Empty;
        if (request.ApiKey is not null)
        {
            settings.SensitiveApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : _protector.Protect(request.ApiKey.Trim());
        }

        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(settings);
    }

    private async Task<AiProviderSettings> GetEntityAsync(CancellationToken cancellationToken)
    {
        var settings = await _dbContext.AiProviderSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        settings = new AiProviderSettings
        {
            Id = Guid.NewGuid(),
            Enabled = false,
            Endpoint = "https://api.openai.com/v1/chat/completions",
            ModelName = "gpt-4.1-mini",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _dbContext.AiProviderSettings.Add(settings);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private static AiSettingsDto ToDto(AiProviderSettings settings)
    {
        return new AiSettingsDto(
            settings.Enabled,
            settings.Endpoint,
            settings.ModelName,
            !string.IsNullOrWhiteSpace(settings.SensitiveApiKey),
            StorageWarning);
    }

    private string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        try { return _protector.Unprotect(value); }
        catch (CryptographicException) { return value; }
    }
}
