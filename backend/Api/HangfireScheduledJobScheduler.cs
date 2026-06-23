using Application.Contracts;
using Application.Models;
using Hangfire;
using Hangfire.Storage.SQLite;
using Infrastructure;

namespace Api;

public sealed class HangfireScheduledJobScheduler : IScheduledJobScheduler
{
    private readonly IRecurringJobManager _recurringJobManager;
    private readonly IBackgroundJobClient _backgroundJobClient;

    public HangfireScheduledJobScheduler(IRecurringJobManager recurringJobManager, IBackgroundJobClient backgroundJobClient)
    {
        _recurringJobManager = recurringJobManager;
        _backgroundJobClient = backgroundJobClient;
    }

    public void Register(ScheduledJobDto job)
    {
        _recurringJobManager.AddOrUpdate<IScheduledJobExecutor>(
            RecurringJobId(job.Id),
            executor => executor.RunAsync(job.Id, null, CancellationToken.None),
            job.CronExpression,
            new RecurringJobOptions { TimeZone = ScheduledJobService.ResolveTimezone(job.Timezone) });
    }

    public void Remove(Guid scheduledJobId) =>
        _recurringJobManager.RemoveIfExists(RecurringJobId(scheduledJobId));

    public string EnqueueRun(Guid scheduledJobId, Guid runId) =>
        _backgroundJobClient.Enqueue<IScheduledJobExecutor>(
            executor => executor.RunAsync(scheduledJobId, runId, CancellationToken.None));

    private static string RecurringJobId(Guid id) => $"scheduled-job:{id:N}";
}

public static class HangfireRegistration
{
    public static IServiceCollection AddScheduledJobHangfire(this IServiceCollection services, IConfiguration configuration, string appDataPath)
    {
        var storagePath = configuration["Hangfire:SQLitePath"]
            ?? Path.Combine(appDataPath, "hangfire.db");
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);

        services.AddHangfire(config => config
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSQLiteStorage(storagePath));
        services.AddHangfireServer(options => options.WorkerCount = 1);
        services.AddSingleton<IScheduledJobScheduler, HangfireScheduledJobScheduler>();
        return services;
    }
}
