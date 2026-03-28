using System.Reflection;
using System.Text.Json;
using GitHub.Copilot.SDK;
using TroubleScout;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests;

public class TroubleshootingSessionByokPricingTests
{
    [Fact]
    public void ParseByokModelsResponse_WithFallbackPricing_AddsEstimatedPrice()
    {
        const string json = """
            {
              "data": [
                {
                  "id": "openai/gpt-4o",
                  "name": "GPT-4o"
                }
              ]
            }
            """;

        using var document = JsonDocument.Parse(json);

        var result = InvokeParseByokModelsResponse(document.RootElement);

        Assert.Single(result.Models);
        Assert.True(result.PricingByModelId.ContainsKey("openai/gpt-4o"));
        Assert.Equal(2.50m, result.PricingByModelId["openai/gpt-4o"].InputPricePerMillionTokens);
        Assert.Equal(10.00m, result.PricingByModelId["openai/gpt-4o"].OutputPricePerMillionTokens);
        Assert.Equal("~$2.5/M in, ~$10/M out", result.PricingByModelId["openai/gpt-4o"].DisplayText);
    }

    [Fact]
    public void ParseByokModelsResponse_WithNonChatFallback_DoesNotAddTokenPricing()
    {
        const string json = """
            {
              "data": [
                {
                  "id": "dall-e-3",
                  "name": "DALL-E 3"
                }
              ]
            }
            """;

        using var document = JsonDocument.Parse(json);

        var result = InvokeParseByokModelsResponse(document.RootElement);

        Assert.Empty(result.Models);
        Assert.False(result.PricingByModelId.ContainsKey("dall-e-3"));
    }

    [Fact]
    public void ParseByokModelsResponse_WithBedrockFallbackPricing_AddsEstimatedPrice()
    {
        const string json = """
            {
              "data": [
                {
                  "id": "bedrock/global.anthropic.claude-sonnet-4-6-v1:0",
                  "name": "Claude Sonnet 4.6"
                },
                {
                  "id": "bedrock/amazon.nova-pro-v1:0",
                  "name": "Amazon Nova Pro"
                }
              ]
            }
            """;

        using var document = JsonDocument.Parse(json);

        var result = InvokeParseByokModelsResponse(document.RootElement);

        Assert.Equal(2, result.Models.Count);
        Assert.Equal("~$3/M in, ~$15/M out", result.PricingByModelId["bedrock/global.anthropic.claude-sonnet-4-6-v1:0"].DisplayText);
        Assert.Equal("~$0.8/M in, ~$3.2/M out", result.PricingByModelId["bedrock/amazon.nova-pro-v1:0"].DisplayText);
    }

    private static (List<ModelInfo> Models, Dictionary<string, TestByokPriceInfo> PricingByModelId) InvokeParseByokModelsResponse(JsonElement rootElement)
    {
        var method = typeof(ModelDiscoveryManager).GetMethod("ParseByokModelsResponse", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);

        var result = method!.Invoke(null, [rootElement]);
        Assert.NotNull(result);

        var resultType = result!.GetType();
        var models = (List<ModelInfo>)resultType.GetProperty("Models")!.GetValue(result)!;
        var pricingObject = resultType.GetProperty("PricingByModelId")!.GetValue(result)!;

        var pricing = new Dictionary<string, TestByokPriceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (System.Collections.IEnumerable)pricingObject)
        {
            var entryType = entry.GetType();
            var key = (string)entryType.GetProperty("Key")!.GetValue(entry)!;
            var value = entryType.GetProperty("Value")!.GetValue(entry)!;
            var valueType = value.GetType();

            pricing[key] = new TestByokPriceInfo(
                (decimal?)valueType.GetProperty("InputPricePerMillionTokens")!.GetValue(value),
                (decimal?)valueType.GetProperty("OutputPricePerMillionTokens")!.GetValue(value),
                (string?)valueType.GetProperty("DisplayText")!.GetValue(value));
        }

        return (models, pricing);
    }

    private sealed record TestByokPriceInfo(decimal? InputPricePerMillionTokens, decimal? OutputPricePerMillionTokens, string? DisplayText);
}
