namespace Application.Models;

public sealed record EnvironmentClearRequest(bool Confirmation, string? CommitMessage);
