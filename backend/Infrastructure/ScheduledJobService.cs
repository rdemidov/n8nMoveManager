using System.IO.Compression;
using System.Text.Json;
using Application;
using Application.Contracts;
using Application.Models;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public sealed class ScheduledJobService : IScheduledJobService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _dbContext;
    private readonly IEnvironmentService _environmentService;
    private readonly IWorkspaceService _workspaceService;
    private readonly IScheduledJobScheduler _scheduler;

    public ScheduledJobService(
        AppDbContext dbContext,
        IEnvironmentService environmentService,
        IWorkspaceService workspaceService,
        IScheduledJobScheduler scheduler)
    {
        _dbContext = dbContext;
        _environmentService = environmentService;
        _workspaceService = workspaceService;
        _scheduler = scheduler;
    }

    public async Task<IReadOnlyList<ScheduledJobDto>> ListAsync(CancellationToken cancellationToken)
    {
        var environments = await _dbContext.Environments.AsNoTracking().ToDictionaryAsync(environment => environment.Id, cancellationToken);
        var lastRuns = (await _dbContext.ScheduledJobRuns.AsNoTracking().ToListAsync(cancellationToken))
            .GroupBy(run => run.ScheduledJobId)
            .Select(group => group.OrderByDescending(run => run.StartedAt).First())
            .ToDictionary(run => run.ScheduledJobId);

        return (await _dbContext.ScheduledJobs.AsNoTracking().OrderBy(job => job.Name).ToListAsync(cancellationToken))
            .Select(job => ToDto(job, environments.GetValueOrDefault(job.EnvironmentId), lastRuns.GetValueOrDefault(job.Id)?.Status))
            .ToArray();
    }

    public async Task<ScheduledJobDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await FindJobAsync(id, cancellationToken);
        var environment = await _dbContext.Environments.AsNoTracking().FirstOrDefaultAsync(item => item.Id == job.EnvironmentId, cancellationToken);
        var lastRunStatus = (await _dbContext.ScheduledJobRuns.AsNoTracking()
                .Where(run => run.ScheduledJobId == id)
                .ToListAsync(cancellationToken))
            .OrderByDescending(run => run.StartedAt)
            .Select(run => run.Status)
            .FirstOrDefault();
        return ToDto(job, environment, lastRunStatus);
    }

    public async Task<ScheduledJobDto> CreateAsync(ScheduledJobRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new ScheduledJob { CreatedAt = now, UpdatedAt = now };
        await ApplyRequestAsync(job, request, cancellationToken);
        _dbContext.ScheduledJobs.Add(job);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = await GetAsync(job.Id, cancellationToken);
        if (dto.IsEnabled)
        {
            _scheduler.Register(dto);
        }

        return dto;
    }

    public async Task<ScheduledJobDto> UpdateAsync(Guid id, ScheduledJobRequest request, CancellationToken cancellationToken)
    {
        var job = await FindJobAsync(id, cancellationToken);
        await ApplyRequestAsync(job, request, cancellationToken);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = await GetAsync(job.Id, cancellationToken);
        if (dto.IsEnabled)
        {
            _scheduler.Register(dto);
        }
        else
        {
            _scheduler.Remove(dto.Id);
        }

        return dto;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await FindJobAsync(id, cancellationToken);
        _dbContext.ScheduledJobs.Remove(job);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _scheduler.Remove(id);
    }

    public async Task<ScheduledJobDto> EnableAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await FindJobAsync(id, cancellationToken);
        job.IsEnabled = true;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        var dto = await GetAsync(id, cancellationToken);
        _scheduler.Register(dto);
        return dto;
    }

    public async Task<ScheduledJobDto> DisableAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await FindJobAsync(id, cancellationToken);
        job.IsEnabled = false;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        _scheduler.Remove(id);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<ScheduledJobRunNowResult> RunNowAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await FindJobAsync(id, cancellationToken);
        var run = new ScheduledJobRun
        {
            ScheduledJobId = job.Id,
            StartedAt = DateTimeOffset.UtcNow,
            Status = "queued",
            Logs = "Manual run queued."
        };
        _dbContext.ScheduledJobRuns.Add(run);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _scheduler.EnqueueRun(job.Id, run.Id);
        return new ScheduledJobRunNowResult(run.Id, "Scheduled job run queued.");
    }

    public async Task<IReadOnlyList<ScheduledJobRunSummaryDto>> ListRunsAsync(Guid id, CancellationToken cancellationToken)
    {
        await FindJobAsync(id, cancellationToken);
        return (await _dbContext.ScheduledJobRuns.AsNoTracking()
            .Where(run => run.ScheduledJobId == id)
            .ToListAsync(cancellationToken))
            .OrderByDescending(run => run.StartedAt)
            .Take(100)
            .Select(run => new ScheduledJobRunSummaryDto(run.Id, run.ScheduledJobId, run.StartedAt, run.FinishedAt, run.Status, run.ErrorMessage, run.CommitSha))
            .ToList();
    }

    public async Task<ScheduledJobRunDto> GetRunAsync(Guid id, Guid runId, CancellationToken cancellationToken)
    {
        await FindJobAsync(id, cancellationToken);
        var run = await _dbContext.ScheduledJobRuns.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ScheduledJobId == id && item.Id == runId, cancellationToken)
            ?? throw new WorkflowImportException("Scheduled job run was not found.");
        return new ScheduledJobRunDto(run.Id, run.ScheduledJobId, run.StartedAt, run.FinishedAt, run.Status, SplitLogs(run.Logs), run.ErrorMessage, run.CommitSha, run.ResultJson);
    }

    public async Task RegisterEnabledJobsAsync(CancellationToken cancellationToken)
    {
        foreach (var job in await ListAsync(cancellationToken))
        {
            if (job.IsEnabled)
            {
                _scheduler.Register(job);
            }
        }
    }

    private async Task ApplyRequestAsync(ScheduledJob job, ScheduledJobRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new WorkflowImportException("Scheduled job name is required.");
        }

        if (!ScheduledJobTypes.All.Contains(request.JobType, StringComparer.OrdinalIgnoreCase))
        {
            throw new WorkflowImportException($"Unsupported scheduled job type '{request.JobType}'.");
        }

        ValidateCron(request.CronExpression);
        ResolveTimezone(request.Timezone);
        var environment = await _dbContext.Environments.AsNoTracking().FirstOrDefaultAsync(item => item.Id == request.EnvironmentId, cancellationToken)
            ?? throw new WorkflowImportException("Selected environment does not exist.");
        var configJson = ValidateConfig(request.JobType, request.ConfigJson);

        job.Name = request.Name.Trim();
        job.JobType = request.JobType.Trim();
        job.EnvironmentId = environment.Id;
        job.CronExpression = request.CronExpression.Trim();
        job.Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "Europe/Kyiv" : request.Timezone.Trim();
        job.IsEnabled = request.IsEnabled;
        job.ConfigJson = configJson;
        job.NextRunAt = ScheduledJobCron.GetNextOccurrence(job.CronExpression, job.Timezone);

        _ = await _environmentService.GetByKeyAsync(environment.Key, cancellationToken);
        _ = await _workspaceService.GetOrCreateDefaultWorkspaceAsync(cancellationToken);
    }

    private async Task<ScheduledJob> FindJobAsync(Guid id, CancellationToken cancellationToken) =>
        await _dbContext.ScheduledJobs.FirstOrDefaultAsync(job => job.Id == id, cancellationToken)
            ?? throw new WorkflowImportException("Scheduled job was not found.");

    private static string ValidateConfig(string jobType, string? configJson)
    {
        var json = string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson;
        try
        {
            if (jobType.Equals(ScheduledJobTypes.DockerN8nWorkflowExport, StringComparison.OrdinalIgnoreCase))
            {
                var config = JsonSerializer.Deserialize<DockerN8nWorkflowExportJobConfig>(json, JsonOptions) ?? new DockerN8nWorkflowExportJobConfig("n8n");
                if (string.IsNullOrWhiteSpace(config.ContainerName))
                {
                    throw new WorkflowImportException("Container name is required for Docker export jobs.");
                }

                if (config.ExportCredentials)
                {
                    throw new WorkflowImportException("Credential export is not allowed for scheduled Docker export jobs.");
                }

                return JsonSerializer.Serialize(config with { ExportWorkflows = true, ExportCredentials = false }, JsonOptions);
            }

            if (jobType.Equals(ScheduledJobTypes.N8nApiWorkflowSync, StringComparison.OrdinalIgnoreCase))
            {
                var config = JsonSerializer.Deserialize<N8nApiWorkflowSyncJobConfig>(json, JsonOptions) ?? new N8nApiWorkflowSyncJobConfig();
                return JsonSerializer.Serialize(config, JsonOptions);
            }

            var backup = JsonSerializer.Deserialize<WorkspaceBackupJobConfig>(json, JsonOptions) ?? new WorkspaceBackupJobConfig();
            if (!backup.IncludeDatabase && !backup.IncludeGitRepo)
            {
                throw new WorkflowImportException("Workspace backup must include the Git repository, the database, or both.");
            }

            return JsonSerializer.Serialize(backup with { RetentionCount = Math.Clamp(backup.RetentionCount, 1, 100) }, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new WorkflowImportException($"Invalid job configuration JSON: {ex.Message}");
        }
    }

    private static void ValidateCron(string cronExpression)
    {
        if (string.IsNullOrWhiteSpace(cronExpression) || cronExpression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 5)
        {
            throw new WorkflowImportException("Cron expression must contain 5 fields.");
        }
    }

    public static TimeZoneInfo ResolveTimezone(string timezone)
    {
        var id = string.IsNullOrWhiteSpace(timezone) ? "Europe/Kyiv" : timezone.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException) when (id.Equals("Europe/Kyiv", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new WorkflowImportException($"Invalid timezone '{id}': {ex.Message}");
        }
        catch (TimeZoneNotFoundException)
        {
            throw new WorkflowImportException($"Invalid timezone '{id}'.");
        }
    }

    private static ScheduledJobDto ToDto(ScheduledJob job, EnvironmentDefinition? environment, string? lastRunStatus) =>
        new(
            job.Id,
            job.Name,
            job.JobType,
            job.EnvironmentId,
            environment?.Key ?? "unknown",
            environment?.Name ?? "Unknown environment",
            job.CronExpression,
            job.Timezone,
            job.IsEnabled,
            job.ConfigJson,
            job.CreatedAt,
            job.UpdatedAt,
            job.LastRunAt,
            job.NextRunAt,
            lastRunStatus);

    private static string[] SplitLogs(string logs) =>
        logs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static class ScheduledJobTypes
{
    public const string DockerN8nWorkflowExport = "DockerN8nWorkflowExport";
    public const string WorkspaceBackup = "WorkspaceBackup";
    public const string N8nApiWorkflowSync = "N8nApiWorkflowSync";
    public static readonly string[] All = [DockerN8nWorkflowExport, N8nApiWorkflowSync, WorkspaceBackup];
}

public static class ScheduledJobCron
{
    public static DateTimeOffset? GetNextOccurrence(string cronExpression, string timezone)
    {
        var zone = ScheduledJobService.ResolveTimezone(timezone);
        var next = Cronos.CronExpression.Parse(cronExpression).GetNextOccurrence(DateTimeOffset.UtcNow, zone);
        return next;
    }
}

public sealed class ScheduledJobExecutor : IScheduledJobExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _dbContext;
    private readonly DockerN8nExportService _dockerExportService;
    private readonly IWorkflowApiSyncService _workflowApiSyncService;
    private readonly IEnvironmentDockerConfigStore _dockerConfigStore;
    private readonly IEnvironmentService _environmentService;
    private readonly IWorkspaceService _workspaceService;
    private readonly IScheduledJobClock _clock;

    public ScheduledJobExecutor(
        AppDbContext dbContext,
        DockerN8nExportService dockerExportService,
        IWorkflowApiSyncService workflowApiSyncService,
        IEnvironmentDockerConfigStore dockerConfigStore,
        IEnvironmentService environmentService,
        IWorkspaceService workspaceService,
        IScheduledJobClock clock)
    {
        _dbContext = dbContext;
        _dockerExportService = dockerExportService;
        _workflowApiSyncService = workflowApiSyncService;
        _dockerConfigStore = dockerConfigStore;
        _environmentService = environmentService;
        _workspaceService = workspaceService;
        _clock = clock;
    }

    public async Task RunAsync(Guid scheduledJobId, Guid? runId = null, CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.ScheduledJobs.FirstOrDefaultAsync(item => item.Id == scheduledJobId, cancellationToken)
            ?? throw new WorkflowImportException("Scheduled job was not found.");
        var run = runId.HasValue
            ? await _dbContext.ScheduledJobRuns.FirstOrDefaultAsync(item => item.Id == runId.Value, cancellationToken)
            : null;
        run ??= new ScheduledJobRun { ScheduledJobId = scheduledJobId, StartedAt = _clock.UtcNow, Status = "queued" };
        if (run.Id == Guid.Empty)
        {
            run.Id = Guid.NewGuid();
        }

        if (_dbContext.Entry(run).State == EntityState.Detached)
        {
            _dbContext.ScheduledJobRuns.Add(run);
        }

        var logs = SplitLogs(run.Logs).ToList();
        run.Status = "running";
        run.StartedAt = _clock.UtcNow;
        logs.Add($"Started at {run.StartedAt:O}.");
        logs.Add($"Job type: {job.JobType}.");
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var environment = await _dbContext.Environments.AsNoTracking().FirstAsync(item => item.Id == job.EnvironmentId, cancellationToken);
            logs.Add($"Selected environment: {environment.Name} ({environment.Key}).");

            object result = job.JobType switch
            {
                ScheduledJobTypes.DockerN8nWorkflowExport => await RunDockerExportAsync(environment.Key, job.ConfigJson, logs, cancellationToken),
                ScheduledJobTypes.N8nApiWorkflowSync => await RunApiSyncAsync(environment.Key, logs, cancellationToken),
                ScheduledJobTypes.WorkspaceBackup => await RunWorkspaceBackupAsync(job.ConfigJson, logs, cancellationToken),
                _ => throw new WorkflowImportException($"Unsupported scheduled job type '{job.JobType}'.")
            };

            run.Status = "success";
            run.ResultJson = JsonSerializer.Serialize(result, JsonOptions);
            if (result is DockerExportResultDto dockerResult)
            {
                run.CommitSha = dockerResult.CommitSha;
            }
            else if (result is WorkflowApiSyncResult apiSyncResult)
            {
                run.CommitSha = apiSyncResult.CommitSha;
            }
        }
        catch (Exception ex) when (ex is WorkflowImportException or IOException or InvalidOperationException or LibGit2Sharp.LibGit2SharpException or JsonException)
        {
            run.Status = "failed";
            run.ErrorMessage = ex.Message;
            logs.Add($"Error: {ex.Message}");
        }
        finally
        {
            run.FinishedAt = _clock.UtcNow;
            job.LastRunAt = run.StartedAt;
            job.NextRunAt = ScheduledJobCron.GetNextOccurrence(job.CronExpression, job.Timezone);
            logs.Add($"Finished at {run.FinishedAt:O}.");
            logs.Add($"Duration: {(run.FinishedAt.Value - run.StartedAt).TotalSeconds:N1} seconds.");
            run.Logs = string.Join('\n', logs);
            await _dbContext.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task<DockerExportResultDto> RunDockerExportAsync(string environmentKey, string configJson, List<string> logs, CancellationToken cancellationToken)
    {
        var config = JsonSerializer.Deserialize<DockerN8nWorkflowExportJobConfig>(configJson, JsonOptions) ?? new DockerN8nWorkflowExportJobConfig("n8n");
        if (config.ExportCredentials)
        {
            throw new WorkflowImportException("Credential export is not allowed.");
        }

        logs.Add($"Docker command started for container '{config.ContainerName}'.");
        logs.Add("Credential export disabled. Workflow export only.");
        await _dockerConfigStore.SaveAsync(environmentKey, new EnvironmentDockerConfigRequest(
            true,
            config.ContainerName,
            "n8n",
            "/tmp/n8nmm-workflows.json",
            null), cancellationToken);
        var result = await _dockerExportService.ExportWorkflowsAsync(environmentKey, cancellationToken);
        logs.AddRange(result.Logs);
        logs.Add($"Number of workflows imported: {result.ImportedWorkflowsCount}.");
        logs.Add($"Changed files count: {result.ChangedFilesCount}.");
        logs.Add(result.CommitSha is null ? "Skipped commit: no workflow changes detected." : $"Commit SHA: {result.CommitSha}.");
        logs.Add($"Credential references scanned: {result.CredentialReferencesScanned}.");
        foreach (var warning in result.Warnings)
        {
            logs.Add($"Warning: {warning}");
        }

        return result;
    }

    private async Task<WorkflowApiSyncResult> RunApiSyncAsync(string environmentKey, List<string> logs, CancellationToken cancellationToken)
    {
        logs.Add("Starting n8n public API workflow sync (read-only against n8n).");
        var result = await _workflowApiSyncService.SyncAsync(environmentKey, cancellationToken);
        logs.Add($"Fetched workflows: {result.FetchedWorkflowsCount}.");
        logs.Add($"Changed files: {result.ChangedFilesCount}.");
        logs.Add(result.CommitSha is null ? "Skipped commit: no workflow changes detected." : $"Commit SHA: {result.CommitSha}.");
        return result;
    }

    private async Task<object> RunWorkspaceBackupAsync(string configJson, List<string> logs, CancellationToken cancellationToken)
    {
        var config = JsonSerializer.Deserialize<WorkspaceBackupJobConfig>(configJson, JsonOptions) ?? new WorkspaceBackupJobConfig();
        var workspace = await _workspaceService.GetOrCreateDefaultWorkspaceAsync(cancellationToken);
        var appDataPath = Directory.GetParent(workspace.RepoPath)?.Parent?.Parent?.FullName
            ?? Path.Combine(AppContext.BaseDirectory, "App_Data");
        var backupsPath = Path.Combine(appDataPath, "backups");
        Directory.CreateDirectory(backupsPath);

        var backupPath = Path.Combine(backupsPath, $"workspace-backup-{_clock.UtcNow:yyyyMMdd-HHmmss}.zip");
        logs.Add($"Backup path: {backupPath}.");
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            if (config.IncludeGitRepo && Directory.Exists(workspace.RepoPath))
            {
                AddDirectory(archive, workspace.RepoPath, "repo", logs);
            }

            if (config.IncludeDatabase)
            {
                foreach (var databasePath in Directory.EnumerateFiles(appDataPath, "n8n-move-manager.db*"))
                {
                    archive.CreateEntryFromFile(databasePath, Path.Combine("database", Path.GetFileName(databasePath)));
                    logs.Add($"Database file copied: {Path.GetFileName(databasePath)}.");
                }
            }
        }

        ApplyRetention(backupsPath, Math.Clamp(config.RetentionCount, 1, 100), logs);
        return new { backupPath, config.IncludeGitRepo, config.IncludeDatabase, config.RetentionCount };
    }

    private static void AddDirectory(ZipArchive archive, string sourcePath, string entryRoot, List<string> logs)
    {
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, file);
            archive.CreateEntryFromFile(file, Path.Combine(entryRoot, relative));
            count++;
        }

        logs.Add($"Git repository files copied: {count}.");
    }

    private static void ApplyRetention(string backupsPath, int retentionCount, List<string> logs)
    {
        var oldBackups = Directory.EnumerateFiles(backupsPath, "workspace-backup-*.zip")
            .OrderByDescending(File.GetCreationTimeUtc)
            .Skip(retentionCount)
            .ToArray();
        foreach (var oldBackup in oldBackups)
        {
            File.Delete(oldBackup);
            logs.Add($"Removed old backup: {Path.GetFileName(oldBackup)}.");
        }
    }

    private static string[] SplitLogs(string logs) =>
        logs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
