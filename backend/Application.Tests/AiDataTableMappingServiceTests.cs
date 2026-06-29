using Application.Contracts;
using Application.Models;
using Xunit;

namespace Application.Tests;

public sealed class AiDataTableMappingServiceTests
{
    [Fact]
    public async Task CreateMappingsAsync_ValidatesAndSavesSuggestedPair()
    {
        var tables = new FakeDataTableService();
        var service = new AiDataTableMappingService(
            new FakeSettingsStore(),
            new FakeAgentClient("""
                {
                  "mappings": [{
                    "sourceTableId": "source-orders",
                    "targetTableId": "target-orders",
                    "reason": "Names and column schemas match.",
                    "confidence": "high"
                  }],
                  "warnings": []
                }
                """),
            tables);

        var result = await service.CreateMappingsAsync(new AiDataTableMappingRequest("local", "prod"), CancellationToken.None);

        Assert.Equal(1, result.AppliedMappingsCount);
        var saved = Assert.Single(tables.SavedMappings);
        Assert.Equal("source-orders", saved.SourceTableId);
        Assert.Equal("target-orders", saved.TargetTableId);
        Assert.True(Assert.Single(result.Items).Applied);
    }

    [Fact]
    public async Task CreateMappingsAsync_DoesNotSaveIdsOutsideSelectedSnapshots()
    {
        var tables = new FakeDataTableService();
        var service = new AiDataTableMappingService(
            new FakeSettingsStore(),
            new FakeAgentClient("""{"mappings":[{"sourceTableId":"source-orders","targetTableId":"invented","reason":"guess","confidence":"low"}],"warnings":[]}"""),
            tables);

        var result = await service.CreateMappingsAsync(new AiDataTableMappingRequest("local", "prod"), CancellationToken.None);

        Assert.Equal(0, result.AppliedMappingsCount);
        Assert.Empty(tables.SavedMappings);
        Assert.Contains("target table ID", Assert.Single(result.Items).SkippedReason);
    }

    private sealed class FakeDataTableService : IDataTableService
    {
        public List<DataTableMappingRequest> SavedMappings { get; } = [];

        public Task<PagedResult<DataTableListItemDto>> ListAsync(string environmentKey, int page, int pageSize, string? search, string? sort, string? direction, CancellationToken cancellationToken)
        {
            var item = environmentKey == "local"
                ? new DataTableListItemDto("source-orders", "Orders", "[{\"name\":\"orderId\",\"type\":\"string\"}]", 10, "local", DateTimeOffset.UtcNow)
                : new DataTableListItemDto("target-orders", "Orders", "[{\"name\":\"orderId\",\"type\":\"string\"}]", 20, "prod", DateTimeOffset.UtcNow);
            return Task.FromResult(new PagedResult<DataTableListItemDto>([item], 1, 1, 100));
        }

        public Task<IReadOnlyList<DataTableMappingDto>> GetMappingsAsync(string sourceEnvironmentKey, string targetEnvironmentKey, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DataTableMappingDto>>([]);

        public Task<DataTableMappingDto> SaveMappingAsync(DataTableMappingRequest request, CancellationToken cancellationToken)
        {
            SavedMappings.Add(request);
            return Task.FromResult(new DataTableMappingDto(Guid.NewGuid(), request.SourceEnvironmentKey, request.TargetEnvironmentKey, request.SourceTableId, "Orders", request.TargetTableId, "Orders", DateTimeOffset.UtcNow));
        }

        public Task DeleteMappingAsync(Guid mappingId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DataTableSyncResult> SyncAsync(string environmentKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DataTableComparisonDto> CompareAsync(string sourceEnvironmentKey, string targetEnvironmentKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DataTablePromotionPlan> GetPromotionPlanAsync(string sourceEnvironmentKey, string targetEnvironmentKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DataTablePromotionApplyResult> ApplyPromotionAsync(DataTablePromotionApplyRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DataTableLiveDeployResult> DeploySchemasAsync(DataTableLiveDeployRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeSettingsStore : IAiProviderSettingsStore
    {
        public Task<AiProviderConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken) => Task.FromResult<AiProviderConfiguration?>(new(true, "https://example.test", "model", "key"));
        public Task<AiSettingsDto> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AiSettingsDto> SaveAsync(AiSettingsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeAgentClient(string response) : IAiAgentClient
    {
        public Task<string> RunJsonAsync(AiProviderConfiguration configuration, string instructions, string prompt, CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
