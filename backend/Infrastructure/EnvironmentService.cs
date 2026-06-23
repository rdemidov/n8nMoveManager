using Application;
using Application.Contracts;
using Application.Models;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public sealed class EnvironmentService : IEnvironmentService
{
    public const string DefaultName = "Local";
    public const string DefaultKey = "local";
    public const string DefaultBranch = "env/local";

    private readonly AppDbContext _dbContext;
    private readonly IWorkspaceService _workspaceService;
    private readonly IGitRepositoryService _gitRepositoryService;

    public EnvironmentService(AppDbContext dbContext, IWorkspaceService workspaceService, IGitRepositoryService gitRepositoryService)
    {
        _dbContext = dbContext;
        _workspaceService = workspaceService;
        _gitRepositoryService = gitRepositoryService;
    }

    public async Task<IReadOnlyList<EnvironmentDto>> ListAsync(CancellationToken cancellationToken)
    {
        var workspace = await _workspaceService.GetOrCreateDefaultWorkspaceAsync(cancellationToken);
        await EnsureDefaultEnvironmentAsync(workspace, cancellationToken);

        return await _dbContext.Environments
            .AsNoTracking()
            .Where(environment => environment.WorkspaceId == workspace.Id)
            .OrderBy(environment => environment.Name)
            .Select(environment => new EnvironmentDto(
                environment.Id,
                environment.Name,
                environment.Key,
                environment.Description,
                environment.GitBranch,
                environment.GitBranch,
                environment.CreatedAt,
                environment.UpdatedAt,
                environment.IsDefault))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<EnvironmentContext> GetByKeyAsync(string environmentKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(environmentKey))
        {
            throw new WorkflowImportException("Environment key is required.");
        }

        var workspace = await _workspaceService.GetOrCreateDefaultWorkspaceAsync(cancellationToken);
        await EnsureDefaultEnvironmentAsync(workspace, cancellationToken);

        var normalizedKey = environmentKey.Trim().ToLowerInvariant();
        var environment = await _dbContext.Environments
            .SingleOrDefaultAsync(item => item.WorkspaceId == workspace.Id && item.Key == normalizedKey, cancellationToken);
        if (environment is null)
        {
            var aliasKey = NormalizeKnownEnvironmentAlias(normalizedKey);
            if (!string.Equals(aliasKey, normalizedKey, StringComparison.Ordinal))
            {
                environment = await _dbContext.Environments
                    .SingleOrDefaultAsync(item => item.WorkspaceId == workspace.Id && item.Key == aliasKey, cancellationToken);
            }
        }

        if (environment is null)
        {
            throw new WorkflowImportException($"Unknown environment key '{environmentKey}'.");
        }

        return new EnvironmentContext(workspace, environment);
    }

    public async Task<EnvironmentDto> CreateAsync(EnvironmentRequest request, CancellationToken cancellationToken)
    {
        var workspace = await _workspaceService.GetOrCreateDefaultWorkspaceAsync(cancellationToken);
        await EnsureDefaultEnvironmentAsync(workspace, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var key = NormalizeKey(string.IsNullOrWhiteSpace(request.Key) ? Slugify(request.Name) : request.Key);
        var branch = NormalizeBranch(string.IsNullOrWhiteSpace(request.GitBranchName) ? $"env/{key}" : request.GitBranchName);
        await ValidateUniqueAsync(workspace.Id, key, branch, null, cancellationToken);

        var environment = new EnvironmentDefinition
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            Name = RequireName(request.Name),
            Key = key,
            Description = request.Description?.Trim(),
            GitBranch = branch,
            CreatedAt = now,
            UpdatedAt = now,
            IsDefault = false
        };

        _dbContext.Environments.Add(environment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _gitRepositoryService.EnsureRepository(workspace.RepoPath);
        _gitRepositoryService.EnsureBranch(workspace.RepoPath, environment.GitBranch);

        return ToDto(environment);
    }

    public async Task<EnvironmentDto> UpdateAsync(string environmentKey, EnvironmentRequest request, CancellationToken cancellationToken)
    {
        var context = await GetByKeyAsync(environmentKey, cancellationToken);
        var environment = context.Environment;
        var key = NormalizeKey(string.IsNullOrWhiteSpace(request.Key) ? environment.Key : request.Key);
        var branch = NormalizeBranch(string.IsNullOrWhiteSpace(request.GitBranchName) ? $"env/{key}" : request.GitBranchName);
        await ValidateUniqueAsync(context.Workspace.Id, key, branch, environment.Id, cancellationToken);

        var oldKey = environment.Key;
        environment.Name = RequireName(request.Name);
        environment.Key = key;
        environment.Description = request.Description?.Trim();
        environment.GitBranch = branch;
        environment.UpdatedAt = DateTimeOffset.UtcNow;

        if (!string.Equals(oldKey, key, StringComparison.OrdinalIgnoreCase))
        {
            await _dbContext.Workflows
                .Where(workflow => workflow.WorkspaceId == context.Workspace.Id && workflow.EnvironmentId == environment.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(workflow => workflow.EnvironmentKey, key), cancellationToken);
            await _dbContext.CredentialReferences
                .Where(reference => reference.WorkspaceId == context.Workspace.Id && reference.EnvironmentId == environment.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(reference => reference.EnvironmentKey, key), cancellationToken);
            await _dbContext.EnvironmentCredentials
                .Where(credential => credential.WorkspaceId == context.Workspace.Id && credential.EnvironmentId == environment.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(credential => credential.EnvironmentKey, key), cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _gitRepositoryService.EnsureRepository(context.Workspace.RepoPath);
        _gitRepositoryService.EnsureBranch(context.Workspace.RepoPath, environment.GitBranch);

        return ToDto(environment);
    }

    public async Task<EnvironmentClearResult> ClearAsync(string environmentKey, string? commitMessage, CancellationToken cancellationToken)
    {
        var context = await GetByKeyAsync(environmentKey, cancellationToken);
        var workspace = context.Workspace;
        var environment = context.Environment;

        _gitRepositoryService.EnsureRepository(workspace.RepoPath);
        _gitRepositoryService.EnsureBranch(workspace.RepoPath, environment.GitBranch);

        var repoFiles = _gitRepositoryService.ReadWorkflowFilesFromBranch(workspace.RepoPath, environment.GitBranch)
            .Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var metadataFiles = await _dbContext.Workflows
            .AsNoTracking()
            .Where(workflow => workflow.WorkspaceId == workspace.Id && workflow.EnvironmentId == environment.Id)
            .Select(workflow => workflow.FilePath)
            .ToArrayAsync(cancellationToken);

        foreach (var path in metadataFiles)
        {
            repoFiles.Add(path);
        }

        foreach (var relativePath in repoFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteRepoFileIfSafe(workspace.RepoPath, relativePath);
        }

        var workflowMetadataCount = await _dbContext.Workflows
            .CountAsync(workflow => workflow.WorkspaceId == workspace.Id && workflow.EnvironmentId == environment.Id, cancellationToken);
        var credentialReferenceCount = await _dbContext.CredentialReferences
            .CountAsync(reference => reference.WorkspaceId == workspace.Id && reference.EnvironmentId == environment.Id, cancellationToken);
        var environmentCredentialCount = await _dbContext.EnvironmentCredentials
            .CountAsync(credential => credential.WorkspaceId == workspace.Id && credential.EnvironmentId == environment.Id, cancellationToken);
        var logicalCredentialIdsToClear = await _dbContext.LogicalCredentialEnvironmentMappings
            .Where(mapping => mapping.WorkspaceId == workspace.Id && mapping.EnvironmentId == environment.Id)
            .Select(mapping => mapping.LogicalCredentialId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        var logicalMappingCount = await _dbContext.LogicalCredentialEnvironmentMappings
            .CountAsync(mapping => mapping.WorkspaceId == workspace.Id && logicalCredentialIdsToClear.Contains(mapping.LogicalCredentialId), cancellationToken);

        var commit = await _gitRepositoryService.CommitChangesAsync(
            workspace.RepoPath,
            environment.Key,
            repoFiles,
            string.IsNullOrWhiteSpace(commitMessage)
                ? $"Clear workflows from {environment.Key}: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}"
                : commitMessage.Trim(),
            cancellationToken);

        _dbContext.LogicalCredentialEnvironmentMappings.RemoveRange(_dbContext.LogicalCredentialEnvironmentMappings.Where(mapping => mapping.WorkspaceId == workspace.Id && logicalCredentialIdsToClear.Contains(mapping.LogicalCredentialId)));
        _dbContext.LogicalCredentials.RemoveRange(_dbContext.LogicalCredentials.Where(credential => credential.WorkspaceId == workspace.Id && logicalCredentialIdsToClear.Contains(credential.Id)));
        _dbContext.CredentialReferences.RemoveRange(_dbContext.CredentialReferences.Where(reference => reference.WorkspaceId == workspace.Id && reference.EnvironmentId == environment.Id));
        _dbContext.EnvironmentCredentials.RemoveRange(_dbContext.EnvironmentCredentials.Where(credential => credential.WorkspaceId == workspace.Id && credential.EnvironmentId == environment.Id));
        _dbContext.Workflows.RemoveRange(_dbContext.Workflows.Where(workflow => workflow.WorkspaceId == workspace.Id && workflow.EnvironmentId == environment.Id));
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new EnvironmentClearResult(
            commit.CommitSha is null ? "Environment cleared. No Git changes were detected." : "Environment cleared and committed.",
            environment.Key,
            repoFiles.Count,
            workflowMetadataCount,
            credentialReferenceCount,
            environmentCredentialCount,
            logicalMappingCount,
            commit.ChangedFilesCount,
            commit.CommitSha,
            commit.CommitSha is null);
    }

    public async Task<EnvironmentDeleteResult> DeleteAsync(string environmentKey, bool force, CancellationToken cancellationToken)
    {
        var context = await GetByKeyAsync(environmentKey, cancellationToken);
        if (context.Environment.IsDefault)
        {
            throw new WorkflowImportException("The default Local environment cannot be deleted.");
        }

        var hasWorkflowHistory = await _dbContext.Workflows
            .AnyAsync(workflow => workflow.WorkspaceId == context.Workspace.Id && workflow.EnvironmentId == context.Environment.Id, cancellationToken);
        if (hasWorkflowHistory && !force)
        {
            throw new WorkflowImportException("Environment has workflow history. Retry with force=true to remove metadata. The Git branch will be kept.");
        }

        var mappings = _dbContext.LogicalCredentialEnvironmentMappings
            .Where(mapping => mapping.WorkspaceId == context.Workspace.Id && mapping.EnvironmentId == context.Environment.Id);
        _dbContext.LogicalCredentialEnvironmentMappings.RemoveRange(mappings);
        _dbContext.CredentialReferences.RemoveRange(_dbContext.CredentialReferences.Where(reference => reference.EnvironmentId == context.Environment.Id));
        _dbContext.EnvironmentCredentials.RemoveRange(_dbContext.EnvironmentCredentials.Where(credential => credential.EnvironmentId == context.Environment.Id));
        _dbContext.EnvironmentDockerConfigs.RemoveRange(_dbContext.EnvironmentDockerConfigs.Where(config => config.EnvironmentId == context.Environment.Id));
        _dbContext.Workflows.RemoveRange(_dbContext.Workflows.Where(workflow => workflow.EnvironmentId == context.Environment.Id));
        _dbContext.Environments.Remove(context.Environment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new EnvironmentDeleteResult("Environment metadata deleted. Git branch was kept.");
    }

    private async Task EnsureDefaultEnvironmentAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.Environments
            .AnyAsync(environment => environment.WorkspaceId == workspace.Id && environment.Key == DefaultKey, cancellationToken);

        if (exists)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _dbContext.Environments.Add(new EnvironmentDefinition
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            Name = DefaultName,
            Key = DefaultKey,
            GitBranch = DefaultBranch,
            CreatedAt = now,
            UpdatedAt = now,
            IsDefault = true
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ValidateUniqueAsync(Guid workspaceId, string key, string branch, Guid? existingId, CancellationToken cancellationToken)
    {
        if (await _dbContext.Environments.AnyAsync(environment =>
            environment.WorkspaceId == workspaceId
            && environment.Key == key
            && environment.Id != existingId, cancellationToken))
        {
            throw new WorkflowImportException($"Environment key '{key}' is already in use.");
        }

        if (await _dbContext.Environments.AnyAsync(environment =>
            environment.WorkspaceId == workspaceId
            && environment.GitBranch == branch
            && environment.Id != existingId, cancellationToken))
        {
            throw new WorkflowImportException($"Git branch '{branch}' is already in use.");
        }
    }

    private static EnvironmentDto ToDto(EnvironmentDefinition environment)
    {
        return new EnvironmentDto(
            environment.Id,
            environment.Name,
            environment.Key,
            environment.Description,
            environment.GitBranch,
            environment.GitBranch,
            environment.CreatedAt,
            environment.UpdatedAt,
            environment.IsDefault);
    }

    private static void DeleteRepoFileIfSafe(string repoPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || !relativePath.StartsWith("workflows/", StringComparison.OrdinalIgnoreCase)
            || !relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var repoRoot = Path.GetFullPath(repoPath);
        var absolutePath = Path.GetFullPath(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!absolutePath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowImportException($"Refusing to clear unsafe repository path '{relativePath}'.");
        }

        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
        }
    }

    private static string RequireName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new WorkflowImportException("Environment name is required.");
        }

        return name.Trim();
    }

    private static string NormalizeKey(string? key)
    {
        var normalized = Slugify(key);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new WorkflowImportException("Environment key is required.");
        }

        return normalized;
    }

    private static string NormalizeKnownEnvironmentAlias(string normalizedKey)
    {
        return normalizedKey switch
        {
            "production" => "prod",
            "development" => "dev",
            _ => normalizedKey
        };
    }

    private static string NormalizeBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            throw new WorkflowImportException("Git branch name is required.");
        }

        return branch.Trim().Replace('\\', '/');
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
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug;
    }
}
