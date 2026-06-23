using Application.Contracts;
using Application.Models;
using Xunit;

namespace Application.Tests;

public sealed class AiCredentialMappingServiceTests
{
    [Fact]
    public async Task CreateMappingsAsync_CreatesLogicalCredentialAndBothEnvironmentMappings()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var logicalService = new FakeLogicalCredentialService();
        var service = new AiCredentialMappingService(
            new FakeSettingsStore(new AiProviderConfiguration(true, "https://example.test", "model", "key")),
            new FakeAgentClient($$"""
            {
              "mappings": [
                {
                  "logicalKey": "report-sql",
                  "displayName": "Report SQL",
                  "sourceEnvironmentCredentialId": "{{sourceId}}",
                  "targetEnvironmentCredentialId": "{{targetId}}",
                  "reason": "Names and types match.",
                  "confidence": "high"
                }
              ],
              "warnings": []
            }
            """),
            new FakeCredentialInventoryService(sourceId, targetId),
            logicalService);

        var result = await service.CreateMappingsAsync(new AiCredentialMappingRequest("local", "prod"), CancellationToken.None);

        Assert.Equal(1, result.AppliedMappingsCount);
        Assert.Equal(1, result.CreatedLogicalCredentialsCount);
        var item = Assert.Single(result.Items);
        Assert.True(item.Applied);
        Assert.Equal("report-sql", item.LogicalKey);
        Assert.Equal(2, logicalService.MappingRequests.Count);
        Assert.Contains(logicalService.MappingRequests, request => request.EnvironmentKey == "local" && request.EnvironmentCredentialId == sourceId);
        Assert.Contains(logicalService.MappingRequests, request => request.EnvironmentKey == "prod" && request.EnvironmentCredentialId == targetId);
    }

    [Fact]
    public async Task CreateMappingsAsync_SkipsAiCredentialIdsOutsideSelectedEnvironments()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var service = new AiCredentialMappingService(
            new FakeSettingsStore(new AiProviderConfiguration(true, "https://example.test", "model", "key")),
            new FakeAgentClient($$"""
            {
              "mappings": [
                {
                  "logicalKey": "report-sql",
                  "displayName": "Report SQL",
                  "sourceEnvironmentCredentialId": "{{sourceId}}",
                  "targetEnvironmentCredentialId": "{{Guid.NewGuid()}}",
                  "reason": "Looks similar.",
                  "confidence": "medium"
                }
              ],
              "warnings": []
            }
            """),
            new FakeCredentialInventoryService(sourceId, targetId),
            new FakeLogicalCredentialService());

        var result = await service.CreateMappingsAsync(new AiCredentialMappingRequest("local", "prod"), CancellationToken.None);

        Assert.Equal(0, result.AppliedMappingsCount);
        var item = Assert.Single(result.Items);
        Assert.False(item.Applied);
        Assert.Contains("target credential ID", item.SkippedReason);
    }

    private sealed class FakeCredentialInventoryService : ICredentialInventoryService
    {
        private readonly Guid _sourceId;
        private readonly Guid _targetId;

        public FakeCredentialInventoryService(Guid sourceId, Guid targetId)
        {
            _sourceId = sourceId;
            _targetId = targetId;
        }

        public Task ReplaceWorkflowReferencesAsync(Guid workspaceId, Guid environmentId, string environmentKey, string workflowFilePath, IReadOnlyCollection<CredentialScanItem> references, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<EnvironmentCredentialDto>> ListEnvironmentCredentialsAsync(string environmentKey, CancellationToken cancellationToken)
        {
            IReadOnlyList<EnvironmentCredentialDto> credentials = environmentKey == "local"
                ? [new EnvironmentCredentialDto(_sourceId, "local", "postgres", "local-db", "Report SQL", 3, DateTimeOffset.UtcNow)]
                : [new EnvironmentCredentialDto(_targetId, "prod", "postgres", "prod-db", "Report SQL", 2, DateTimeOffset.UtcNow)];
            return Task.FromResult(credentials);
        }

        public Task<IReadOnlyList<CredentialReferenceDto>> ListCredentialReferencesAsync(string environmentKey, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<CredentialReferenceDto>>([]);
        }
    }

    private sealed class FakeLogicalCredentialService : ILogicalCredentialService
    {
        private readonly List<LogicalCredentialDto> _credentials = [];

        public List<LogicalCredentialMappingRequest> MappingRequests { get; } = [];

        public Task<IReadOnlyList<LogicalCredentialDto>> ListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<LogicalCredentialDto>>(_credentials);
        }

        public Task<LogicalCredentialDto> CreateAsync(LogicalCredentialRequest request, CancellationToken cancellationToken)
        {
            var credential = new LogicalCredentialDto(Guid.NewGuid(), request.Key, request.DisplayName, []);
            _credentials.Add(credential);
            return Task.FromResult(credential);
        }

        public Task<LogicalCredentialDto> UpdateAsync(Guid id, LogicalCredentialRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<LogicalCredentialDto> SetMappingAsync(LogicalCredentialMappingRequest request, CancellationToken cancellationToken)
        {
            MappingRequests.Add(request);
            return Task.FromResult(_credentials.Single(credential => credential.Id == request.LogicalCredentialId));
        }

        public Task<LogicalCredentialDto> SetPairMappingAsync(LogicalCredentialPairMappingRequest request, CancellationToken cancellationToken)
        {
            MappingRequests.Add(new LogicalCredentialMappingRequest(request.LogicalCredentialId, request.SourceEnvironmentKey, request.SourceEnvironmentCredentialId));
            MappingRequests.Add(new LogicalCredentialMappingRequest(request.LogicalCredentialId, request.TargetEnvironmentKey, request.TargetEnvironmentCredentialId));
            return Task.FromResult(_credentials.Single(credential => credential.Id == request.LogicalCredentialId));
        }

        public Task DeleteMappingAsync(Guid mappingId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeSettingsStore : IAiProviderSettingsStore
    {
        private readonly AiProviderConfiguration? _configuration;

        public FakeSettingsStore(AiProviderConfiguration? configuration)
        {
            _configuration = configuration;
        }

        public Task<AiSettingsDto> GetAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new AiSettingsDto(false, string.Empty, string.Empty, false, string.Empty));
        }

        public Task<AiProviderConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_configuration);
        }

        public Task<AiSettingsDto> SaveAsync(AiSettingsRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AiSettingsDto(request.Enabled, request.Endpoint ?? string.Empty, request.ModelName ?? string.Empty, false, string.Empty));
        }
    }

    private sealed class FakeAgentClient : IAiAgentClient
    {
        private readonly string _response;

        public FakeAgentClient(string response)
        {
            _response = response;
        }

        public Task<string> RunJsonAsync(AiProviderConfiguration configuration, string instructions, string prompt, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }
}
