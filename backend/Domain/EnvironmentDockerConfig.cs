namespace Domain;

public sealed class EnvironmentDockerConfig
{
    public Guid EnvironmentId { get; set; }
    public bool DockerEnabled { get; set; }
    public string ContainerName { get; set; } = string.Empty;
    public string N8nCliCommand { get; set; } = "n8n";
    public string TempContainerPath { get; set; } = "/tmp/n8nmm-workflows.json";
    public string? TempHostImportPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
