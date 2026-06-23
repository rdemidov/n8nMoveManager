using Application;
using Application.Contracts;
using Application.Models;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public sealed class LogicalCredentialService : ILogicalCredentialService, ICredentialMappingReader
{
    private readonly AppDbContext _dbContext;
    private readonly IWorkspaceService _workspaceService;
    private readonly IEnvironmentService _environmentService;

    public LogicalCredentialService(
        AppDbContext dbContext,
        IWorkspaceService workspaceService,
        IEnvironmentService environmentService)
    {
        _dbContext = dbContext;
        _workspaceService = workspaceService;
        _environmentService = environmentService;
    }

    public async Task<IReadOnlyList<LogicalCredentialDto>> ListAsync(CancellationToken cancellationToken)
    {
        var workspace = await _workspaceService.GetOrCreateDefaultWorkspaceAsync(cancellationToken);
        var logicalCredentials = await _dbContext.LogicalCredentials
            .AsNoTracking()
            .Where(credential => credential.WorkspaceId == workspace.Id)
            .OrderBy(credential => credential.Key)
            .ToArrayAsync(cancellationToken);

        var results = new List<LogicalCredentialDto>();
        foreach (var credential in logicalCredentials)
        {
            results.Add(await ToDtoAsync(credential.Id, cancellationToken));
        }

        return results;
    }

    public async Task<LogicalCredentialDto> CreateAsync(LogicalCredentialRequest request, CancellationToken cancellationToken)
    {
        var workspace = await _workspaceService.GetOrCreateDefaultWorkspaceAsync(cancellationToken);
        var key = Slugify(request.Key);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new WorkflowImportException("Logical credential key and display name are required.");
        }

        if (await _dbContext.LogicalCredentials.AnyAsync(item => item.WorkspaceId == workspace.Id && item.Key == key, cancellationToken))
        {
            throw new WorkflowImportException($"Logical credential key '{key}' is already in use.");
        }

        var now = DateTimeOffset.UtcNow;
        var credential = new LogicalCredential
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            Key = key,
            DisplayName = request.DisplayName.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.LogicalCredentials.Add(credential);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(credential.Id, cancellationToken);
    }

    public async Task<LogicalCredentialDto> UpdateAsync(Guid id, LogicalCredentialRequest request, CancellationToken cancellationToken)
    {
        var workspace = await _workspaceService.GetOrCreateDefaultWorkspaceAsync(cancellationToken);
        var credential = await _dbContext.LogicalCredentials.SingleOrDefaultAsync(item => item.WorkspaceId == workspace.Id && item.Id == id, cancellationToken)
            ?? throw new WorkflowImportException("Logical credential was not found.");
        var key = Slugify(request.Key);
        if (await _dbContext.LogicalCredentials.AnyAsync(item => item.WorkspaceId == workspace.Id && item.Key == key && item.Id != id, cancellationToken))
        {
            throw new WorkflowImportException($"Logical credential key '{key}' is already in use.");
        }

        credential.Key = key;
        credential.DisplayName = request.DisplayName.Trim();
        credential.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(credential.Id, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspace = await _workspaceService.GetOrCreateDefaultWorkspaceAsync(cancellationToken);
        _dbContext.LogicalCredentialEnvironmentMappings.RemoveRange(
            _dbContext.LogicalCredentialEnvironmentMappings.Where(mapping => mapping.WorkspaceId == workspace.Id && mapping.LogicalCredentialId == id));
        _dbContext.LogicalCredentials.RemoveRange(
            _dbContext.LogicalCredentials.Where(credential => credential.WorkspaceId == workspace.Id && credential.Id == id));
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<LogicalCredentialDto> SetMappingAsync(LogicalCredentialMappingRequest request, CancellationToken cancellationToken)
    {
        var workspace = await _workspaceService.GetOrCreateDefaultWorkspaceAsync(cancellationToken);
        var environment = (await _environmentService.GetByKeyAsync(request.EnvironmentKey, cancellationToken)).Environment;
        var logical = await _dbContext.LogicalCredentials.SingleOrDefaultAsync(item => item.WorkspaceId == workspace.Id && item.Id == request.LogicalCredentialId, cancellationToken)
            ?? throw new WorkflowImportException("Logical credential was not found.");
        var environmentCredential = await _dbContext.EnvironmentCredentials.SingleOrDefaultAsync(item =>
            item.WorkspaceId == workspace.Id
            && item.EnvironmentId == environment.Id
            && item.Id == request.EnvironmentCredentialId,
            cancellationToken)
            ?? throw new WorkflowImportException("Environment credential was not found.");

        var now = DateTimeOffset.UtcNow;
        var mapping = await _dbContext.LogicalCredentialEnvironmentMappings.SingleOrDefaultAsync(item =>
            item.WorkspaceId == workspace.Id
            && item.LogicalCredentialId == logical.Id
            && item.EnvironmentId == environment.Id,
            cancellationToken);

        if (mapping is null)
        {
            mapping = new LogicalCredentialEnvironmentMapping
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                LogicalCredentialId = logical.Id,
                EnvironmentId = environment.Id,
                CreatedAt = now
            };
            _dbContext.LogicalCredentialEnvironmentMappings.Add(mapping);
        }

        mapping.EnvironmentCredentialId = environmentCredential.Id;
        mapping.UpdatedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(logical.Id, cancellationToken);
    }

    public async Task<LogicalCredentialDto> SetPairMappingAsync(LogicalCredentialPairMappingRequest request, CancellationToken cancellationToken)
    {
        if (string.Equals(request.SourceEnvironmentKey, request.TargetEnvironmentKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowImportException("Source and target environments must differ.");
        }

        var workspace = await _workspaceService.GetOrCreateDefaultWorkspaceAsync(cancellationToken);
        var sourceEnvironment = (await _environmentService.GetByKeyAsync(request.SourceEnvironmentKey, cancellationToken)).Environment;
        var targetEnvironment = (await _environmentService.GetByKeyAsync(request.TargetEnvironmentKey, cancellationToken)).Environment;
        var logical = await _dbContext.LogicalCredentials.SingleOrDefaultAsync(item => item.WorkspaceId == workspace.Id && item.Id == request.LogicalCredentialId, cancellationToken)
            ?? throw new WorkflowImportException("Logical credential was not found.");
        var sourceCredential = await GetEnvironmentCredentialAsync(workspace.Id, sourceEnvironment.Id, request.SourceEnvironmentCredentialId, cancellationToken);
        var targetCredential = await GetEnvironmentCredentialAsync(workspace.Id, targetEnvironment.Id, request.TargetEnvironmentCredentialId, cancellationToken);

        if (!string.Equals(sourceCredential.CredentialType, targetCredential.CredentialType, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowImportException($"Credential types differ: {sourceCredential.CredentialType} vs {targetCredential.CredentialType}.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await UpsertMappingAsync(workspace.Id, logical.Id, sourceEnvironment.Id, sourceCredential.Id, now, cancellationToken);
        await UpsertMappingAsync(workspace.Id, logical.Id, targetEnvironment.Id, targetCredential.Id, now, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ToDtoAsync(logical.Id, cancellationToken);
    }

    public async Task DeleteMappingAsync(Guid mappingId, CancellationToken cancellationToken)
    {
        var workspace = await _workspaceService.GetOrCreateDefaultWorkspaceAsync(cancellationToken);
        _dbContext.LogicalCredentialEnvironmentMappings.RemoveRange(
            _dbContext.LogicalCredentialEnvironmentMappings.Where(mapping => mapping.WorkspaceId == workspace.Id && mapping.Id == mappingId));
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CredentialEnvironmentPair>> GetMappingsAsync(Guid sourceEnvironmentId, Guid targetEnvironmentId, CancellationToken cancellationToken)
    {
        var workspace = await _workspaceService.GetOrCreateDefaultWorkspaceAsync(cancellationToken);
        var rows = await (
            from logical in _dbContext.LogicalCredentials.AsNoTracking()
            join sourceMapping in _dbContext.LogicalCredentialEnvironmentMappings.AsNoTracking()
                on logical.Id equals sourceMapping.LogicalCredentialId
            join targetMapping in _dbContext.LogicalCredentialEnvironmentMappings.AsNoTracking()
                on logical.Id equals targetMapping.LogicalCredentialId
            join sourceCredential in _dbContext.EnvironmentCredentials.AsNoTracking()
                on sourceMapping.EnvironmentCredentialId equals sourceCredential.Id
            join targetCredential in _dbContext.EnvironmentCredentials.AsNoTracking()
                on targetMapping.EnvironmentCredentialId equals targetCredential.Id
            where logical.WorkspaceId == workspace.Id
                && sourceMapping.EnvironmentId == sourceEnvironmentId
                && targetMapping.EnvironmentId == targetEnvironmentId
            select new CredentialEnvironmentPair(
                logical.Key,
                new EnvironmentCredentialSnapshot(sourceCredential.Id, sourceCredential.EnvironmentId, sourceCredential.CredentialType, sourceCredential.CredentialId, sourceCredential.CredentialName),
                new EnvironmentCredentialSnapshot(targetCredential.Id, targetCredential.EnvironmentId, targetCredential.CredentialType, targetCredential.CredentialId, targetCredential.CredentialName)))
            .ToArrayAsync(cancellationToken);

        return rows;
    }

    private async Task<LogicalCredentialDto> ToDtoAsync(Guid id, CancellationToken cancellationToken)
    {
        var credential = await _dbContext.LogicalCredentials.AsNoTracking().SingleAsync(item => item.Id == id, cancellationToken);
        var mappings = await (
            from mapping in _dbContext.LogicalCredentialEnvironmentMappings.AsNoTracking()
            join environment in _dbContext.Environments.AsNoTracking() on mapping.EnvironmentId equals environment.Id
            join environmentCredential in _dbContext.EnvironmentCredentials.AsNoTracking() on mapping.EnvironmentCredentialId equals environmentCredential.Id
            where mapping.LogicalCredentialId == id
            orderby environment.Name
            select new LogicalCredentialMappingDto(
                mapping.Id,
                environment.Id,
                environment.Key,
                environment.Name,
                environmentCredential.Id,
                environmentCredential.CredentialType,
                environmentCredential.CredentialId,
                environmentCredential.CredentialName,
                _dbContext.CredentialReferences.Count(reference =>
                    reference.EnvironmentId == environment.Id
                    && reference.CredentialType == environmentCredential.CredentialType
                    && reference.CredentialId == environmentCredential.CredentialId
                    && reference.CredentialName == environmentCredential.CredentialName),
                environmentCredential.LastDetectedAt))
            .ToArrayAsync(cancellationToken);

        return new LogicalCredentialDto(credential.Id, credential.Key, credential.DisplayName, mappings);
    }

    private static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<EnvironmentCredential> GetEnvironmentCredentialAsync(Guid workspaceId, Guid environmentId, Guid credentialId, CancellationToken cancellationToken)
    {
        return await _dbContext.EnvironmentCredentials.SingleOrDefaultAsync(item =>
            item.WorkspaceId == workspaceId
            && item.EnvironmentId == environmentId
            && item.Id == credentialId,
            cancellationToken)
            ?? throw new WorkflowImportException("Environment credential was not found.");
    }

    private async Task UpsertMappingAsync(Guid workspaceId, Guid logicalCredentialId, Guid environmentId, Guid environmentCredentialId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var mapping = await _dbContext.LogicalCredentialEnvironmentMappings.SingleOrDefaultAsync(item =>
            item.WorkspaceId == workspaceId
            && item.LogicalCredentialId == logicalCredentialId
            && item.EnvironmentId == environmentId,
            cancellationToken);

        if (mapping is null)
        {
            mapping = new LogicalCredentialEnvironmentMapping
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                LogicalCredentialId = logicalCredentialId,
                EnvironmentId = environmentId,
                CreatedAt = now
            };
            _dbContext.LogicalCredentialEnvironmentMappings.Add(mapping);
        }

        mapping.EnvironmentCredentialId = environmentCredentialId;
        mapping.UpdatedAt = now;
    }
}
