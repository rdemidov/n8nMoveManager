namespace Application.Contracts;

public interface IAiAgentClient
{
    Task<string> RunJsonAsync(AiProviderConfiguration configuration, string instructions, string prompt, CancellationToken cancellationToken);
}
