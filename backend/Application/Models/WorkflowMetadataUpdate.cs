namespace Application.Models;

public sealed record WorkflowMetadataUpdate(
    Guid WorkspaceId,
    Guid EnvironmentId,
    string EnvironmentKey,
    string? ExternalId,
    string Name,
    bool Active,
    int NodesCount,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string FilePath,
    DateTimeOffset LastImportedAt);
