namespace Application;

public sealed record WorkflowUploadPayload(IReadOnlyCollection<WorkflowUploadSource> Sources, string? CommitMessage);
