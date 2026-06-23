using Application;
using Application.Contracts;
using Application.Models;
using Domain;
using Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Application.Tests;

public sealed class ScheduledJobServiceTests
{
    [Fact]
    public async Task CreateAsync_StoresJobAndRegisters_WhenEnabled()
    {
        await using var fixture = await ScheduledJobFixture.CreateAsync();
        var service = fixture.CreateService();

        var job = await service.CreateAsync(fixture.Request(), CancellationToken.None);

        Assert.Equal("Daily export", job.Name);
        Assert.Equal("DockerN8nWorkflowExport", job.JobType);
        Assert.True(job.IsEnabled);
        Assert.Single(fixture.Scheduler.Registered);
    }

    [Fact]
    public async Task UpdateAsync_ReplacesRecurringJob_WhenEnabled()
    {
        await using var fixture = await ScheduledJobFixture.CreateAsync();
        var service = fixture.CreateService();
        var job = await service.CreateAsync(fixture.Request(), CancellationToken.None);

        var updated = await service.UpdateAsync(job.Id, fixture.Request(name: "Hourly export", cron: "0 * * * *"), CancellationToken.None);

        Assert.Equal("Hourly export", updated.Name);
        Assert.Equal("0 * * * *", updated.CronExpression);
        Assert.Equal(2, fixture.Scheduler.Registered.Count);
    }

    [Fact]
    public async Task DisableAndEnable_UpdateHangfireRegistration()
    {
        await using var fixture = await ScheduledJobFixture.CreateAsync();
        var service = fixture.CreateService();
        var job = await service.CreateAsync(fixture.Request(), CancellationToken.None);

        await service.DisableAsync(job.Id, CancellationToken.None);
        await service.EnableAsync(job.Id, CancellationToken.None);

        Assert.Contains(job.Id, fixture.Scheduler.Removed);
        Assert.Equal(2, fixture.Scheduler.Registered.Count);
    }

    [Fact]
    public async Task RunNow_CreatesQueuedRunAndEnqueues()
    {
        await using var fixture = await ScheduledJobFixture.CreateAsync();
        var service = fixture.CreateService();
        var job = await service.CreateAsync(fixture.Request(), CancellationToken.None);

        var result = await service.RunNowAsync(job.Id, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.RunId);
        Assert.Contains(result.RunId, fixture.Scheduler.EnqueuedRuns);
        Assert.Equal("queued", (await service.GetRunAsync(job.Id, result.RunId, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task CreateAsync_RejectsInvalidCron()
    {
        await using var fixture = await ScheduledJobFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<WorkflowImportException>(() =>
            service.CreateAsync(fixture.Request(cron: "not cron"), CancellationToken.None));

        Assert.Contains("Cron expression", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_RejectsInvalidEnvironment()
    {
        await using var fixture = await ScheduledJobFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<WorkflowImportException>(() =>
            service.CreateAsync(fixture.Request(environmentId: Guid.NewGuid()), CancellationToken.None));

        Assert.Contains("environment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ScheduledJobFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IConfiguration _configuration;

        private ScheduledJobFixture(SqliteConnection connection, AppDbContext dbContext, IConfiguration configuration, Guid environmentId)
        {
            _connection = connection;
            DbContext = dbContext;
            _configuration = configuration;
            EnvironmentId = environmentId;
        }

        public AppDbContext DbContext { get; }
        public Guid EnvironmentId { get; }
        public FakeScheduledJobScheduler Scheduler { get; } = new();

        public static async Task<ScheduledJobFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var dbContext = new AppDbContext(options);
            await dbContext.Database.EnsureCreatedAsync();

            var workspaceId = Guid.NewGuid();
            var environmentId = Guid.NewGuid();
            var repoPath = Path.Combine(Path.GetTempPath(), "n8nmm-scheduled-tests", Guid.NewGuid().ToString("N"), "repo");
            Directory.CreateDirectory(repoPath);
            dbContext.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Default", RepoPath = repoPath, CreatedAt = DateTimeOffset.UtcNow });
            dbContext.Environments.Add(new EnvironmentDefinition
            {
                Id = environmentId,
                WorkspaceId = workspaceId,
                Name = "Local",
                Key = "local",
                GitBranch = "env/local",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsDefault = true
            });
            await dbContext.SaveChangesAsync();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspace:RepoPath"] = repoPath })
                .Build();
            return new ScheduledJobFixture(connection, dbContext, configuration, environmentId);
        }

        public ScheduledJobService CreateService()
        {
            var workspaceService = new WorkspaceService(DbContext, _configuration);
            var environmentService = new EnvironmentService(DbContext, workspaceService, new GitRepositoryService());
            return new ScheduledJobService(DbContext, environmentService, workspaceService, Scheduler);
        }

        public ScheduledJobRequest Request(string name = "Daily export", string cron = "0 21 * * *", Guid? environmentId = null) =>
            new(
                name,
                "DockerN8nWorkflowExport",
                environmentId ?? EnvironmentId,
                cron,
                "Europe/Kyiv",
                true,
                """{"containerName":"n8n","exportWorkflows":true,"exportCredentials":false,"commitChanges":true,"scanCredentials":true,"deleteTempFiles":true}""");

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakeScheduledJobScheduler : IScheduledJobScheduler
    {
        public List<Guid> Removed { get; } = [];
        public List<Guid> EnqueuedRuns { get; } = [];
        public List<ScheduledJobDto> Registered { get; } = [];

        public void Register(ScheduledJobDto job) => Registered.Add(job);
        public void Remove(Guid scheduledJobId) => Removed.Add(scheduledJobId);

        public string EnqueueRun(Guid scheduledJobId, Guid runId)
        {
            EnqueuedRuns.Add(runId);
            return runId.ToString("N");
        }
    }
}
