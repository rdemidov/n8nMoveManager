using Application;
using Application.Contracts;
using Application.Models;
using Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace Infrastructure;

public sealed class EnvironmentN8nApiConfigStore : IEnvironmentN8nApiConfigStore
{
    private const string DefaultDataTablesPath = "/api/v1/data-tables";
    private readonly AppDbContext _dbContext;
    private readonly IEnvironmentService _environmentService;
    private readonly IDataProtector _protector;

    public EnvironmentN8nApiConfigStore(AppDbContext dbContext, IEnvironmentService environmentService, IDataProtectionProvider dataProtectionProvider)
    {
        _dbContext = dbContext;
        _environmentService = environmentService;
        _protector = dataProtectionProvider.CreateProtector("n8n-move-manager.environment-n8n-api-key.v1");
    }

    public async Task<EnvironmentN8nApiConfigDto> GetAsync(string environmentKey, CancellationToken cancellationToken)
    {
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        var config = await _dbContext.EnvironmentN8nApiConfigs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.EnvironmentId == context.Environment.Id, cancellationToken);
        return config is null
            ? new EnvironmentN8nApiConfigDto(context.Environment.Id, context.Environment.Key, false, string.Empty, DefaultDataTablesPath, null, "/api/v1/workflows", false, DateTimeOffset.MinValue, DateTimeOffset.MinValue)
            : ToDto(context.Environment.Key, config);
    }

    public async Task<EnvironmentN8nApiConfigDto> SaveAsync(string environmentKey, EnvironmentN8nApiConfigRequest request, CancellationToken cancellationToken)
    {
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        var config = await _dbContext.EnvironmentN8nApiConfigs.SingleOrDefaultAsync(item => item.EnvironmentId == context.Environment.Id, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (config is null)
        {
            config = new EnvironmentN8nApiConfig { EnvironmentId = context.Environment.Id, CreatedAt = now };
            _dbContext.EnvironmentN8nApiConfigs.Add(config);
        }

        config.Enabled = request.Enabled;
        config.BaseUrl = (request.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        config.DataTablesPath = NormalizePath(request.DataTablesPath);
        config.DataTablesWritePathTemplate = string.IsNullOrWhiteSpace(request.DataTablesWritePathTemplate) ? null : NormalizePath(request.DataTablesWritePathTemplate);
        config.WorkflowApiPath = NormalizePath(request.WorkflowApiPath ?? "/api/v1/workflows");
        if (!string.IsNullOrWhiteSpace(request.ApiKey)) config.ApiKey = _protector.Protect(request.ApiKey.Trim());
        config.UpdatedAt = now;
        if (config.Enabled && (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new WorkflowImportException("A valid http(s) n8n base URL is required when API integration is enabled.");
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(context.Environment.Key, config);
    }

    public async Task<string?> GetApiKeyAsync(string environmentKey, CancellationToken cancellationToken)
    {
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        var config = await _dbContext.EnvironmentN8nApiConfigs.AsNoTracking().SingleOrDefaultAsync(item => item.EnvironmentId == context.Environment.Id, cancellationToken)
            ?? throw new WorkflowImportException("Configure the n8n API connection first.");
        return Unprotect(config.ApiKey);
    }

    private static EnvironmentN8nApiConfigDto ToDto(string environmentKey, EnvironmentN8nApiConfig item) =>
        new(item.EnvironmentId, environmentKey, item.Enabled, item.BaseUrl, item.DataTablesPath, item.DataTablesWritePathTemplate, item.WorkflowApiPath, !string.IsNullOrWhiteSpace(item.ApiKey), item.CreatedAt, item.UpdatedAt);
    private static string NormalizePath(string? value) => string.IsNullOrWhiteSpace(value) ? DefaultDataTablesPath : "/" + value.Trim().Trim('/');
    private string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        try { return _protector.Unprotect(value); }
        catch (CryptographicException) { return value; } // legacy local value; it is re-protected on its next save
    }
}
