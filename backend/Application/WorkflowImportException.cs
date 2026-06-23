namespace Application;

public sealed class WorkflowImportException : Exception
{
    public WorkflowImportException(string message)
        : base(message)
    {
    }
}
