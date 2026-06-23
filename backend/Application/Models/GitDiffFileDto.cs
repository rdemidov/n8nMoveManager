namespace Application.Models;

public sealed record GitDiffFileDto(
    string FilePath,
    string Status,
    int LinesAdded,
    int LinesDeleted,
    string Patch);
