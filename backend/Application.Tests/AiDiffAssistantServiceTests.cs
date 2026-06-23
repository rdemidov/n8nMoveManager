using Application.Contracts;
using Application.Models;
using Xunit;

namespace Application.Tests;

public sealed class AiDiffAssistantServiceTests
{
    [Fact]
    public async Task AskAssistantAsync_ReturnsClearError_WhenProviderDisabled()
    {
        var service = new AiDiffAssistantService(
            new FakeSettingsStore(null),
            new FakeAgentClient(),
            new AiContextBuilder());

        var ex = await Assert.ThrowsAsync<WorkflowImportException>(() => service.AskAssistantAsync(
            new AiAskRequest("What changed?", "current workflow diff", null, null, null, null, null, null, null),
            CancellationToken.None));

        Assert.Contains("disabled", ex.Message);
    }

    [Fact]
    public async Task AskAssistantAsync_ReturnsClearError_WhenProviderMissingApiKey()
    {
        var service = new AiDiffAssistantService(
            new FakeSettingsStore(new AiProviderConfiguration(true, "https://example.test", "model", "")),
            new FakeAgentClient(),
            new AiContextBuilder());

        var ex = await Assert.ThrowsAsync<WorkflowImportException>(() => service.AskAssistantAsync(
            new AiAskRequest("What changed?", "current workflow diff", null, null, null, null, null, null, null),
            CancellationToken.None));

        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public async Task AskAssistantAsync_UsesN8NMoveManagerAgentSystemPrompt()
    {
        var agentClient = new FakeAgentClient("""{"answer":"Reviewed.","summary":"Reviewed.","importantChanges":[],"risks":[],"suggestedNextStep":"Human review.","confidence":"medium"}""");
        var service = new AiDiffAssistantService(
            new FakeSettingsStore(new AiProviderConfiguration(true, "https://example.test", "model", "key")),
            agentClient,
            new AiContextBuilder());

        var response = await service.AskAssistantAsync(
            new AiAskRequest("What changed?", "current workflow diff", null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.Equal("medium", response.Confidence);
        Assert.Contains("n8n Move Manager", agentClient.Instructions);
        Assert.Contains("not allowed to apply changes", agentClient.Instructions);
        Assert.Contains("HTTP endpoints", agentClient.Instructions);
        Assert.Contains("AI Agent tool nodes", agentClient.Instructions);
        Assert.Contains("use-source", agentClient.Instructions);
        Assert.Contains("manual-review", agentClient.Instructions);
        Assert.Contains("Always include", agentClient.Instructions);
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

        public FakeAgentClient(string response = "{}")
        {
            _response = response;
        }

        public string Instructions { get; private set; } = string.Empty;

        public Task<string> RunJsonAsync(AiProviderConfiguration configuration, string instructions, string prompt, CancellationToken cancellationToken)
        {
            Instructions = instructions;
            return Task.FromResult(_response);
        }
    }
}
