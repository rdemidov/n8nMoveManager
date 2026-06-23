namespace Domain;

public sealed class AiProviderSettings
{
    public Guid Id { get; set; }
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string? SensitiveApiKey { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
