using System.Reflection;
using System.Text.Json;
using GitHub.Copilot.SDK;
using TroubleScout;
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

        Assert.Single(result.Models);
        Assert.False(result.PricingByModelId.ContainsKey("dall-e-3"));
    }

    private static (List<ModelInfo> Models, Dictionary<string, TestByokPriceInfo> PricingByModelId) InvokeParseByokModelsResponse(JsonElement rootElement)
    {
        var method = typeof(TroubleshootingSession).GetMethod("ParseByokModelsResponse", BindingFlags.Static | BindingFlags.NonPublic);

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
