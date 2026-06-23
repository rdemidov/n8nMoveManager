namespace Domain;

public sealed class Workspace
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RepoPath { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
