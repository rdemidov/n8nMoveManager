namespace Domain;

public sealed class EnvironmentCredential
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string EnvironmentKey { get; set; } = string.Empty;
    public string CredentialType { get; set; } = string.Empty;
    public string? CredentialId { get; set; }
    public string? CredentialName { get; set; }
    public DateTimeOffset FirstDetectedAt { get; set; }
    public DateTimeOffset LastDetectedAt { get; set; }
}
