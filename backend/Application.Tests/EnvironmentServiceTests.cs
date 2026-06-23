using Application.Contracts;
using Domain;
using Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Application.Tests;

public sealed class EnvironmentServiceTests
{
    [Fact]
    public async Task GetByKeyAsync_ResolvesProductionAlias_WhenProdEnvironmentExists()
    {
        await using var fixture = await EnvironmentServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var context = await service.GetByKeyAsync("production", CancellationToken.None);

        Assert.Equal("prod", context.Environment.Key);
        Assert.Equal("Prod", context.Environment.Name);
    }

    [Fact]
    public async Task ClearAsync_RemovesWholeLogicalCredentialMappingGroup_WhenEnvironmentIsCleared()
    {
        await using var fixture = await EnvironmentServiceFixture.CreateAsync();
        await fixture.SeedLogicalCredentialMappingPairAsync();
        var service = fixture.CreateService();

        var result = await service.ClearAsync("prod", null, CancellationToken.None);

        Assert.Equal(2, result.RemovedLogicalCredentialMappingsCount);
        Assert.False(await fixture.DbContext.LogicalCredentialEnvironmentMappings.AnyAsync());
        Assert.False(await fixture.DbContext.LogicalCredentials.AnyAsync());
        Assert.False(await fixture.DbContext.EnvironmentCredentials.AnyAsync(credential => credential.EnvironmentId == fixture.ProdEnvironmentId));
        Assert.True(await fixture.DbContext.EnvironmentCredentials.AnyAsync(credential => credential.EnvironmentId == fixture.LocalEnvironmentId));
    }

    private sealed class EnvironmentServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IConfiguration _configuration;

        private EnvironmentServiceFixture(SqliteConnection connection, AppDbContext dbContext, IConfiguration configuration, Guid localEnvironmentId, Guid prodEnvironmentId)
        {
            _connection = connection;
            DbContext = dbContext;
            _configuration = configuration;
            LocalEnvironmentId = localEnvironmentId;
            ProdEnvironmentId = prodEnvironmentId;
        }

        public AppDbContext DbContext { get; }
        public Guid LocalEnvironmentId { get; }
        public Guid ProdEnvironmentId { get; }

        public static async Task<EnvironmentServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var dbContext = new AppDbContext(options);
            await dbContext.Database.EnsureCreatedAsync();

            var workspaceId = Guid.NewGuid();
            var localEnvironmentId = Guid.NewGuid();
            var prodEnvironmentId = Guid.NewGuid();
            var repoPath = Path.Combine(Path.GetTempPath(), "n8nmm-environment-tests", Guid.NewGuid().ToString("N"), "repo");
            Directory.CreateDirectory(repoPath);
            dbContext.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Default", RepoPath = repoPath, CreatedAt = DateTimeOffset.UtcNow });
            dbContext.Environments.Add(new EnvironmentDefinition
            {
                Id = localEnvironmentId,
                WorkspaceId = workspaceId,
                Name = "Local",
                Key = "local",
                GitBranch = "env/local",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsDefault = true
            });
            dbContext.Environments.Add(new EnvironmentDefinition
            {
                Id = prodEnvironmentId,
                WorkspaceId = workspaceId,
                Name = "Prod",
                Key = "prod",
                GitBranch = "env/prod",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsDefault = false
            });
            await dbContext.SaveChangesAsync();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspace:RepoPath"] = repoPath })
                .Build();
            return new EnvironmentServiceFixture(connection, dbContext, configuration, localEnvironmentId, prodEnvironmentId);
        }

        public async Task SeedLogicalCredentialMappingPairAsync()
        {
            var workspaceId = await DbContext.Workspaces.Select(workspace => workspace.Id).SingleAsync();
            var logicalCredentialId = Guid.NewGuid();
            var localCredentialId = Guid.NewGuid();
            var prodCredentialId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            DbContext.LogicalCredentials.Add(new LogicalCredential
            {
                Id = logicalCredentialId,
                WorkspaceId = workspaceId,
                Key = "api-token",
                DisplayName = "API token",
                CreatedAt = now,
                UpdatedAt = now
            });
            DbContext.EnvironmentCredentials.AddRange(
                new EnvironmentCredential
                {
                    Id = localCredentialId,
                    WorkspaceId = workspaceId,
                    EnvironmentId = LocalEnvironmentId,
                    EnvironmentKey = "local",
                    CredentialType = "httpHeaderAuth",
                    CredentialId = "local-token",
                    CredentialName = "Local token",
                    FirstDetectedAt = now,
                    LastDetectedAt = now
                },
                new EnvironmentCredential
                {
                    Id = prodCredentialId,
                    WorkspaceId = workspaceId,
                    EnvironmentId = ProdEnvironmentId,
                    EnvironmentKey = "prod",
                    CredentialType = "httpHeaderAuth",
                    CredentialId = "prod-token",
                    CredentialName = "Prod token",
                    FirstDetectedAt = now,
                    LastDetectedAt = now
                });
            DbContext.LogicalCredentialEnvironmentMappings.AddRange(
                new LogicalCredentialEnvironmentMapping
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    LogicalCredentialId = logicalCredentialId,
                    EnvironmentId = LocalEnvironmentId,
                    EnvironmentCredentialId = localCredentialId,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new LogicalCredentialEnvironmentMapping
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    LogicalCredentialId = logicalCredentialId,
                    EnvironmentId = ProdEnvironmentId,
                    EnvironmentCredentialId = prodCredentialId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            await DbContext.SaveChangesAsync();
        }

        public EnvironmentService CreateService()
        {
            var workspaceService = new WorkspaceService(DbContext, _configuration);
            return new EnvironmentService(DbContext, workspaceService, new GitRepositoryService());
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
