using Application.Models;

namespace Application.Contracts;

public interface IDockerCommandRunner
{
    Task<DockerCommandResult> CheckDockerAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken);
    Task<DockerCommandResult> CheckContainerExistsAsync(string containerName, TimeSpan timeout, CancellationToken cancellationToken);
    Task<DockerCommandResult> DockerExecAsync(string containerName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
    Task<DockerCommandResult> DockerCpAsync(string containerSourcePath, string hostDestinationPath, TimeSpan timeout, CancellationToken cancellationToken);
}
