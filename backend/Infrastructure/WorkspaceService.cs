using Application.Contracts;
using Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Infrastructure;

public sealed class WorkspaceService : IWorkspaceService
{
    private readonly AppDbContext _dbContext;
    private readonly IConfiguration _configuration;

    public WorkspaceService(AppDbContext dbContext, IConfiguration configuration)
    {
        _dbContext = dbContext;
        _configuration = configuration;
    }

    public async Task<Workspace> GetOrCreateDefaultWorkspaceAsync(CancellationToken cancellationToken)
    {
        await _dbContext.Database.EnsureCreatedAsync(cancellationToken);

        var workspace = await _dbContext.Workspaces
            .FirstOrDefaultAsync(cancellationToken);

        if (workspace is not null)
        {
            Directory.CreateDirectory(workspace.RepoPath);
            return workspace;
        }

        var repoPath = _configuration["Workspace:RepoPath"];
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            repoPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "App_Data", "workspaces", Guid.NewGuid().ToString("N"), "repo"));
        }

        workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "Default Workspace",
            RepoPath = repoPath,
            CreatedAt = DateTimeOffset.UtcNow
        };

        Directory.CreateDirectory(workspace.RepoPath);
        _dbContext.Workspaces.Add(workspace);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return workspace;
    }
}
