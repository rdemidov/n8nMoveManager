using Application.Contracts;
using Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? "Data Source=App_Data/n8n-move-manager.db";

        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IWorkspaceService, WorkspaceService>();
        services.AddScoped<IEnvironmentService, EnvironmentService>();
        services.AddScoped<IWorkflowMetadataService, WorkflowMetadataService>();
        services.AddScoped<IGitRepositoryService, GitRepositoryService>();
        services.AddScoped<ICredentialInventoryService, CredentialInventoryService>();
        services.AddScoped<IPromotionAuditService, PromotionAuditService>();
        services.AddScoped<IPromotionBaselineService, PromotionBaselineService>();
        services.AddScoped<IRestoreAuditService, RestoreAuditService>();
        services.AddScoped<IAiProviderSettingsStore, AiProviderSettingsStore>();
        services.AddScoped<IAiAgentClient, MicrosoftAgentFrameworkClient>();
        services.AddScoped<IEnvironmentDockerConfigStore, EnvironmentDockerConfigStore>();
        services.AddScoped<IEnvironmentN8nApiConfigStore, EnvironmentN8nApiConfigStore>();
        services.AddScoped<ILocalUserService, LocalUserService>();
        services.AddScoped<IDataTableService, DataTableService>();
        services.AddScoped<IWorkflowDeploymentService, WorkflowDeploymentService>();
        services.AddScoped<IWorkflowApiSyncService, WorkflowApiSyncService>();
        services.AddScoped<IWorkflowHealthService, WorkflowHealthService>();
        services.AddHttpClient("n8n", client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IDockerCommandRunner, DockerCommandRunner>();
        services.AddSingleton<IScheduledJobClock, ScheduledJobClock>();
        services.AddScoped<IScheduledJobService, ScheduledJobService>();
        services.AddScoped<IScheduledJobExecutor, ScheduledJobExecutor>();
        services.AddScoped<LogicalCredentialService>();
        services.AddScoped<ILogicalCredentialService>(provider => provider.GetRequiredService<LogicalCredentialService>());
        services.AddScoped<ICredentialMappingReader>(provider => provider.GetRequiredService<LogicalCredentialService>());

        return services;
    }
}
