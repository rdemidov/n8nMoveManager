using Application;
using Application.Contracts;
using Application.Models;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public sealed class EnvironmentDockerConfigStore : IEnvironmentDockerConfigStore
{
    private const string DefaultCli = "n8n";
    private const string DefaultContainerPath = "/tmp/n8nmm-workflows.json";

    private readonly AppDbContext _dbContext;
    private readonly IEnvironmentService _environmentService;

    public EnvironmentDockerConfigStore(AppDbContext dbContext, IEnvironmentService environmentService)
    {
        _dbContext = dbContext;
        _environmentService = environmentService;
    }

    public async Task<EnvironmentDockerConfigDto> GetAsync(string environmentKey, CancellationToken cancellationToken)
    {
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        var config = await _dbContext.EnvironmentDockerConfigs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.EnvironmentId == context.Environment.Id, cancellationToken);

        return config is null
            ? new EnvironmentDockerConfigDto(context.Environment.Id, context.Environment.Key, false, string.Empty, DefaultCli, DefaultContainerPath, null, DateTimeOffset.MinValue, DateTimeOffset.MinValue)
            : ToDto(context.Environment.Key, config);
    }

    public async Task<EnvironmentDockerConfigDto> SaveAsync(string environmentKey, EnvironmentDockerConfigRequest request, CancellationToken cancellationToken)
    {
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var config = await _dbContext.EnvironmentDockerConfigs
            .SingleOrDefaultAsync(item => item.EnvironmentId == context.Environment.Id, cancellationToken);

        if (config is null)
        {
            config = new EnvironmentDockerConfig
            {
                EnvironmentId = context.Environment.Id,
                CreatedAt = now
            };
            _dbContext.EnvironmentDockerConfigs.Add(config);
        }

        config.DockerEnabled = request.DockerEnabled;
        config.ContainerName = Normalize(request.ContainerName);
        config.N8nCliCommand = Normalize(request.N8nCliCommand, DefaultCli);
        config.TempContainerPath = Normalize(request.TempContainerPath, DefaultContainerPath);
        config.TempHostImportPath = string.IsNullOrWhiteSpace(request.TempHostImportPath) ? null : request.TempHostImportPath.Trim();
        config.UpdatedAt = now;

        if (config.DockerEnabled && string.IsNullOrWhiteSpace(config.ContainerName))
        {
            throw new WorkflowImportException("Container name is required when Docker integration is enabled.");
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(context.Environment.Key, config);
    }

    private static EnvironmentDockerConfigDto ToDto(string environmentKey, EnvironmentDockerConfig config) =>
        new(
            config.EnvironmentId,
            environmentKey,
            config.DockerEnabled,
            config.ContainerName,
            config.N8nCliCommand,
            config.TempContainerPath,
            config.TempHostImportPath,
            config.CreatedAt,
            config.UpdatedAt);

    private static string Normalize(string? value, string fallback = "") =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
