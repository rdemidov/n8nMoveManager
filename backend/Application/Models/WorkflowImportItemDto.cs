namespace Application.Models;

public sealed record WorkflowImportItemDto(
    string? Id,
    string Name,
    bool Active,
    int NodesCount,
    string FilePath);
