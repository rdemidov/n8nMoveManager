namespace Application.Models;

public sealed record GitCommitDto(
    string Sha,
    string ShortSha,
    string Message,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset When);
