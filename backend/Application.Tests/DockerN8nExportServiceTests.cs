using Application;
using Application.Contracts;
using Application.Models;
using Xunit;

namespace Application.Tests;

public sealed class DockerN8nExportServiceTests
{
    [Fact]
    public async Task ExportWorkflows_Throws_WhenDockerUnavailable()
    {
        var service = CreateService(docker: new FakeDockerRunner { DockerAvailable = Result(127, stderr: "docker not found") });

        var ex = await Assert.ThrowsAsync<WorkflowImportException>(() => service.ExportWorkflowsAsync("local", CancellationToken.None));

        Assert.Contains("Docker CLI", ex.Message);
    }

    [Fact]
    public async Task ExportWorkflows_Throws_WhenContainerNotFound()
    {
        var service = CreateService(docker: new FakeDockerRunner { ContainerExists = Result(1, stderr: "No such container") });

        var ex = await Assert.ThrowsAsync<WorkflowImportException>(() => service.ExportWorkflowsAsync("local", CancellationToken.None));

        Assert.Contains("Container 'n8n'", ex.Message);
    }

    [Fact]
    public async Task ExportWorkflows_Throws_WhenN8nExportFails()
    {
        var service = CreateService(docker: new FakeDockerRunner { ExecResult = Result(1, stderr: "n8n failed") });

        var ex = await Assert.ThrowsAsync<WorkflowImportException>(() => service.ExportWorkflowsAsync("local", CancellationToken.None));

        Assert.Contains("workflow export command failed", ex.Message);
    }

    [Fact]
    public async Task ExportWorkflows_Throws_WhenDockerCopyFails()
    {
        var service = CreateService(docker: new FakeDockerRunner { CopyResult = Result(1, stderr: "copy failed") });

        var ex = await Assert.ThrowsAsync<WorkflowImportException>(() => service.ExportWorkflowsAsync("local", CancellationToken.None));

        Assert.Contains("Docker copy failed", ex.Message);
    }

    [Fact]
    public async Task ExportWorkflows_ImportsExportedJson()
    {
        var importer = new FakeWorkflowImportService
        {
            Result = new UploadResultDto(1, 1, "abc123", "Import", "Workflow import committed.", [], 2)
        };
        var service = CreateService(importer: importer);

        var result = await service.ExportWorkflowsAsync("local", CancellationToken.None);

        Assert.Equal("imported", result.Status);
        Assert.Equal(1, result.ExportedWorkflowsCount);
        Assert.Equal(1, result.ImportedWorkflowsCount);
        Assert.Equal("abc123", result.CommitSha);
        Assert.Equal(2, result.CredentialReferencesScanned);
        Assert.Contains(importer.LastContent, content => content.Contains("\"nodes\"", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExportWorkflows_SkipsCommit_WhenImportHasNoChanges()
    {
        var importer = new FakeWorkflowImportService
        {
            Result = new UploadResultDto(1, 0, null, null, "No changes were detected. No commit was created.", [], 0)
        };
        var service = CreateService(importer: importer);

        var result = await service.ExportWorkflowsAsync("local", CancellationToken.None);

        Assert.Equal("no-changes", result.Status);
        Assert.True(result.SkippedCommit);
        Assert.Null(result.CommitSha);
        Assert.Contains(result.Warnings, warning => warning.Contains("No changes", StringComparison.OrdinalIgnoreCase));
    }

    private static DockerN8nExportService CreateService(
        FakeDockerRunner? docker = null,
        FakeWorkflowImportService? importer = null)
    {
        return new DockerN8nExportService(
            new FakeDockerConfigStore(),
            docker ?? new FakeDockerRunner(),
            importer ?? new FakeWorkflowImportService());
    }

    private static DockerCommandResult Result(int exitCode = 0, string stdout = "", string stderr = "") =>
        new(exitCode, stdout, stderr, TimeSpan.FromMilliseconds(12), "docker test");

    private sealed class FakeDockerConfigStore : IEnvironmentDockerConfigStore
    {
        public Task<EnvironmentDockerConfigDto> GetAsync(string environmentKey, CancellationToken cancellationToken) =>
            Task.FromResult(new EnvironmentDockerConfigDto(
                Guid.NewGuid(),
                environmentKey,
                true,
                "n8n",
                "n8n",
                "/tmp/n8nmm-workflows.json",
                Path.Combine(Path.GetTempPath(), $"n8nmm-test-{Guid.NewGuid():N}.json"),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));

        public Task<EnvironmentDockerConfigDto> SaveAsync(string environmentKey, EnvironmentDockerConfigRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDockerRunner : IDockerCommandRunner
    {
        public DockerCommandResult DockerAvailable { get; set; } = Result(stdout: "Docker version 1.0");
        public DockerCommandResult ContainerExists { get; set; } = Result(stdout: "true");
        public DockerCommandResult ExecResult { get; set; } = Result(stdout: "exported");
        public DockerCommandResult CopyResult { get; set; } = Result();

        public Task<DockerCommandResult> CheckDockerAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(DockerAvailable);
        public Task<DockerCommandResult> CheckContainerExistsAsync(string containerName, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(ContainerExists);
        public Task<DockerCommandResult> DockerExecAsync(string containerName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(ExecResult);

        public async Task<DockerCommandResult> DockerCpAsync(string containerSourcePath, string hostDestinationPath, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (CopyResult.Succeeded)
            {
                await File.WriteAllTextAsync(hostDestinationPath, """
                {
                  "id": "workflow-1",
                  "name": "Workflow 1",
                  "nodes": [],
                  "connections": {}
                }
                """, cancellationToken);
            }

            return CopyResult;
        }
    }

    private sealed class FakeWorkflowImportService : IWorkflowImportService
    {
        public UploadResultDto Result { get; set; } = new(1, 1, "abc123", "Import", "Workflow import committed.", [], 0);
        public List<string> LastContent { get; } = [];

        public Task<UploadResultDto> ImportAsync(string environmentKey, IReadOnlyCollection<WorkflowUploadSource> sources, string? commitMessage, CancellationToken cancellationToken)
        {
            LastContent.AddRange(sources.Select(source => source.Content));
            return Task.FromResult(Result);
        }
    }
}
