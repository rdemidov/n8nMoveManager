namespace Domain;

/// <summary>Connection settings for the n8n public API in one managed environment.</summary>
public sealed class EnvironmentN8nApiConfig
{
    public Guid EnvironmentId { get; set; }
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string DataTablesPath { get; set; } = "/api/v1/data-tables";
    public string? DataTablesWritePathTemplate { get; set; }
    public string WorkflowApiPath { get; set; } = "/api/v1/workflows";
    public string? ApiKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
