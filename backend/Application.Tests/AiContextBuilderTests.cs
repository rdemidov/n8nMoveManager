using Application.Models;
using Xunit;

namespace Application.Tests;

public sealed class AiContextBuilderTests
{
    [Fact]
    public void BuildAsk_MasksObviousSecretValues()
    {
        var builder = new AiContextBuilder();

        var context = builder.BuildAsk(new AiAskRequest(
            "Does this leak Bearer abcdefghijklmnopqrstuvwxyz123456?",
            "current workflow diff",
            null,
            null,
            null,
            null,
            null,
            null,
            null));

        var json = context.Context.ToJsonString();

        Assert.Contains("[masked secret]", json);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz123456", json);
    }

    [Fact]
    public void ToSafeNode_ExcludesCredentialSecretLikeFields()
    {
        var builder = new AiContextBuilder();
        var unsafeContext = new
        {
            credentials = new
            {
                http = new
                {
                    id = "cred-1",
                    name = "Production HTTP",
                    type = "httpHeaderAuth",
                    password = "super-secret",
                    apiKey = "sk-abcdefghijklmnopqrstuvwxyz"
                }
            }
        };

        var json = builder.ToSafeNode(unsafeContext)!.ToJsonString();

        Assert.Contains("Production HTTP", json);
        Assert.Contains("httpHeaderAuth", json);
        Assert.DoesNotContain("super-secret", json);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz", json);
        Assert.DoesNotContain("apiKey", json);
    }
}
