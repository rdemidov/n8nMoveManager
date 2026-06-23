using Domain;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<EnvironmentDefinition> Environments => Set<EnvironmentDefinition>();
    public DbSet<WorkflowMetadata> Workflows => Set<WorkflowMetadata>();
    public DbSet<CredentialReference> CredentialReferences => Set<CredentialReference>();
    public DbSet<EnvironmentCredential> EnvironmentCredentials => Set<EnvironmentCredential>();
    public DbSet<LogicalCredential> LogicalCredentials => Set<LogicalCredential>();
    public DbSet<LogicalCredentialEnvironmentMapping> LogicalCredentialEnvironmentMappings => Set<LogicalCredentialEnvironmentMapping>();
    public DbSet<PromotionAuditLog> PromotionAuditLogs => Set<PromotionAuditLog>();
    public DbSet<PromotionComparisonBaseline> PromotionComparisonBaselines => Set<PromotionComparisonBaseline>();
    public DbSet<RestoreAuditLog> RestoreAuditLogs => Set<RestoreAuditLog>();
    public DbSet<AiProviderSettings> AiProviderSettings => Set<AiProviderSettings>();
    public DbSet<EnvironmentDockerConfig> EnvironmentDockerConfigs => Set<EnvironmentDockerConfig>();
    public DbSet<EnvironmentN8nApiConfig> EnvironmentN8nApiConfigs => Set<EnvironmentN8nApiConfig>();
    public DbSet<DataTableSnapshot> DataTableSnapshots => Set<DataTableSnapshot>();
    public DbSet<DataTableDeploymentAudit> DataTableDeploymentAudits => Set<DataTableDeploymentAudit>();
    public DbSet<ScheduledJob> ScheduledJobs => Set<ScheduledJob>();
    public DbSet<ScheduledJobRun> ScheduledJobRuns => Set<ScheduledJobRun>();
    public DbSet<LocalUser> LocalUsers => Set<LocalUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.HasKey(workspace => workspace.Id);
            entity.Property(workspace => workspace.Name).HasMaxLength(200).IsRequired();
            entity.Property(workspace => workspace.RepoPath).HasMaxLength(1024).IsRequired();
        });

        modelBuilder.Entity<WorkflowMetadata>(entity =>
        {
            entity.HasKey(workflow => workflow.Id);
            entity.Property(workflow => workflow.EnvironmentKey).HasMaxLength(100).IsRequired();
            entity.Property(workflow => workflow.ExternalId).HasMaxLength(200);
            entity.Property(workflow => workflow.Name).HasMaxLength(300).IsRequired();
            entity.Property(workflow => workflow.FilePath).HasMaxLength(1024).IsRequired();
            entity.HasIndex(workflow => new { workflow.WorkspaceId, workflow.EnvironmentKey, workflow.FilePath }).IsUnique();
            entity.HasIndex(workflow => new { workflow.WorkspaceId, workflow.EnvironmentKey, workflow.ExternalId });
        });

        modelBuilder.Entity<EnvironmentDefinition>(entity =>
        {
            entity.HasKey(environment => environment.Id);
            entity.Property(environment => environment.Name).HasMaxLength(200).IsRequired();
            entity.Property(environment => environment.Key).HasMaxLength(100).IsRequired();
            entity.Property(environment => environment.Description).HasMaxLength(1000);
            entity.Property(environment => environment.GitBranch).HasMaxLength(200).IsRequired();
            entity.HasIndex(environment => new { environment.WorkspaceId, environment.Key }).IsUnique();
            entity.HasIndex(environment => new { environment.WorkspaceId, environment.GitBranch }).IsUnique();
        });

        modelBuilder.Entity<CredentialReference>(entity =>
        {
            entity.HasKey(reference => reference.Id);
            entity.Property(reference => reference.EnvironmentKey).HasMaxLength(100).IsRequired();
            entity.Property(reference => reference.WorkflowExternalId).HasMaxLength(200);
            entity.Property(reference => reference.WorkflowName).HasMaxLength(300).IsRequired();
            entity.Property(reference => reference.WorkflowFilePath).HasMaxLength(1024).IsRequired();
            entity.Property(reference => reference.NodeId).HasMaxLength(200);
            entity.Property(reference => reference.NodeName).HasMaxLength(300).IsRequired();
            entity.Property(reference => reference.NodeType).HasMaxLength(300).IsRequired();
            entity.Property(reference => reference.CredentialType).HasMaxLength(200).IsRequired();
            entity.Property(reference => reference.CredentialId).HasMaxLength(300);
            entity.Property(reference => reference.CredentialName).HasMaxLength(300);
            entity.HasIndex(reference => new { reference.WorkspaceId, reference.EnvironmentId, reference.WorkflowFilePath });
        });

        modelBuilder.Entity<EnvironmentCredential>(entity =>
        {
            entity.HasKey(credential => credential.Id);
            entity.Property(credential => credential.EnvironmentKey).HasMaxLength(100).IsRequired();
            entity.Property(credential => credential.CredentialType).HasMaxLength(200).IsRequired();
            entity.Property(credential => credential.CredentialId).HasMaxLength(300);
            entity.Property(credential => credential.CredentialName).HasMaxLength(300);
            entity.HasIndex(credential => new
            {
                credential.WorkspaceId,
                credential.EnvironmentId,
                credential.CredentialType,
                credential.CredentialId,
                credential.CredentialName
            }).IsUnique();
        });

        modelBuilder.Entity<LogicalCredential>(entity =>
        {
            entity.HasKey(credential => credential.Id);
            entity.Property(credential => credential.Key).HasMaxLength(100).IsRequired();
            entity.Property(credential => credential.DisplayName).HasMaxLength(200).IsRequired();
            entity.HasIndex(credential => new { credential.WorkspaceId, credential.Key }).IsUnique();
        });

        modelBuilder.Entity<LogicalCredentialEnvironmentMapping>(entity =>
        {
            entity.HasKey(mapping => mapping.Id);
            entity.HasIndex(mapping => new { mapping.WorkspaceId, mapping.LogicalCredentialId, mapping.EnvironmentId }).IsUnique();
        });

        modelBuilder.Entity<PromotionAuditLog>(entity =>
        {
            entity.HasKey(log => log.Id);
            entity.Property(log => log.SourceEnvironmentKey).HasMaxLength(100).IsRequired();
            entity.Property(log => log.TargetEnvironmentKey).HasMaxLength(100).IsRequired();
            entity.Property(log => log.Status).HasMaxLength(40).IsRequired();
            entity.Property(log => log.CommitSha).HasMaxLength(80);
            entity.Property(log => log.Summary).HasMaxLength(4000);
            entity.HasIndex(log => new { log.WorkspaceId, log.CreatedAt });
        });

        modelBuilder.Entity<PromotionComparisonBaseline>(entity =>
        {
            entity.HasKey(baseline => baseline.Id);
            entity.Property(baseline => baseline.CommitSha).HasMaxLength(64).IsRequired();
            entity.Property(baseline => baseline.Label).HasMaxLength(200);
            entity.HasIndex(baseline => new { baseline.WorkspaceId, baseline.SourceEnvironmentId, baseline.TargetEnvironmentId }).IsUnique();
        });

        modelBuilder.Entity<RestoreAuditLog>(entity =>
        {
            entity.HasKey(log => log.Id);
            entity.Property(log => log.EnvironmentKey).HasMaxLength(100).IsRequired();
            entity.Property(log => log.RestoreType).HasMaxLength(40).IsRequired();
            entity.Property(log => log.SourceCommitSha).HasMaxLength(80).IsRequired();
            entity.Property(log => log.NewCommitSha).HasMaxLength(80);
            entity.Property(log => log.FilePath).HasMaxLength(1024);
            entity.Property(log => log.Status).HasMaxLength(40).IsRequired();
            entity.Property(log => log.Warnings).HasMaxLength(4000);
            entity.Property(log => log.Errors).HasMaxLength(4000);
            entity.HasIndex(log => new { log.WorkspaceId, log.EnvironmentId, log.CreatedAt });
        });

        modelBuilder.Entity<AiProviderSettings>(entity =>
        {
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.Endpoint).HasMaxLength(1000);
            entity.Property(settings => settings.ModelName).HasMaxLength(200);
            entity.Property(settings => settings.SensitiveApiKey).HasMaxLength(4000);
        });

        modelBuilder.Entity<EnvironmentDockerConfig>(entity =>
        {
            entity.HasKey(config => config.EnvironmentId);
            entity.Property(config => config.ContainerName).HasMaxLength(300);
            entity.Property(config => config.N8nCliCommand).HasMaxLength(200).IsRequired();
            entity.Property(config => config.TempContainerPath).HasMaxLength(1000).IsRequired();
            entity.Property(config => config.TempHostImportPath).HasMaxLength(1000);
        });

        modelBuilder.Entity<EnvironmentN8nApiConfig>(entity =>
        {
            entity.HasKey(config => config.EnvironmentId);
            entity.Property(config => config.BaseUrl).HasMaxLength(1000).IsRequired();
            entity.Property(config => config.DataTablesPath).HasMaxLength(500).IsRequired();
            entity.Property(config => config.DataTablesWritePathTemplate).HasMaxLength(500);
            entity.Property(config => config.ApiKey).HasMaxLength(4000);
        });

        modelBuilder.Entity<DataTableSnapshot>(entity =>
        {
            entity.HasKey(table => table.Id);
            entity.Property(table => table.EnvironmentKey).HasMaxLength(100).IsRequired();
            entity.Property(table => table.ExternalId).HasMaxLength(300).IsRequired();
            entity.Property(table => table.Name).HasMaxLength(300).IsRequired();
            entity.Property(table => table.ColumnsJson).IsRequired();
            entity.HasIndex(table => new { table.EnvironmentId, table.ExternalId }).IsUnique();
            entity.HasIndex(table => new { table.WorkspaceId, table.EnvironmentKey, table.Name });
        });

        modelBuilder.Entity<ScheduledJob>(entity =>
        {
            entity.HasKey(job => job.Id);
            entity.Property(job => job.Name).HasMaxLength(200).IsRequired();
            entity.Property(job => job.JobType).HasMaxLength(100).IsRequired();
            entity.Property(job => job.CronExpression).HasMaxLength(120).IsRequired();
            entity.Property(job => job.Timezone).HasMaxLength(120).IsRequired();
            entity.Property(job => job.ConfigJson).HasMaxLength(8000).IsRequired();
            entity.HasIndex(job => job.EnvironmentId);
            entity.HasIndex(job => job.JobType);
        });

        modelBuilder.Entity<ScheduledJobRun>(entity =>
        {
            entity.HasKey(run => run.Id);
            entity.Property(run => run.Status).HasMaxLength(40).IsRequired();
            entity.Property(run => run.ErrorMessage).HasMaxLength(4000);
            entity.Property(run => run.CommitSha).HasMaxLength(80);
            entity.HasIndex(run => new { run.ScheduledJobId, run.StartedAt });
        });

        modelBuilder.Entity<LocalUser>(entity =>
        {
            entity.HasKey(user => user.Id);
            entity.Property(user => user.UserName).HasMaxLength(200).IsRequired();
            entity.Property(user => user.PasswordHash).HasMaxLength(1000).IsRequired();
            entity.Property(user => user.Role).HasMaxLength(40).IsRequired();
            entity.HasIndex(user => user.UserName).IsUnique();
        });
    }
}
