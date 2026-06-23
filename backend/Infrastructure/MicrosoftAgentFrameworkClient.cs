using Application.Contracts;
using Microsoft.Agents.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace Infrastructure;

public sealed class MicrosoftAgentFrameworkClient : IAiAgentClient
{
    public async Task<string> RunJsonAsync(
        AiProviderConfiguration configuration,
        string instructions,
        string prompt,
        CancellationToken cancellationToken)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = NormalizeEndpoint(configuration.Endpoint)
        };

        var chatClient = new ChatClient(
            configuration.ModelName,
            new ApiKeyCredential(configuration.ApiKey),
            options);

        AIAgent agent = chatClient.AsAIAgent(
            instructions: instructions,
            name: "N8nMoveManagerAgent",
            description: "Explains n8n workflow diffs, promotion plans, credential mappings, and merge conflicts.");

        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken);
        return string.IsNullOrWhiteSpace(response.Text)
            ? response.ToString()
            : response.Text;
    }

    private static Uri NormalizeEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        trimmed = RemoveKnownSuffix(trimmed, "/chat/completions");
        trimmed = RemoveKnownSuffix(trimmed, "/responses");
        return new Uri(trimmed, UriKind.Absolute);
    }

    private static string RemoveKnownSuffix(string endpoint, string suffix)
    {
        return endpoint.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? endpoint[..^suffix.Length]
            : endpoint;
    }
}
