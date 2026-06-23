namespace Application.Models;

public sealed record WorkflowListItemDto(
    string? Id,
    string Name,
    bool Active,
    int NodesCount,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string EnvironmentKey,
    string FilePath,
    DateTimeOffset LastImportedAt);
