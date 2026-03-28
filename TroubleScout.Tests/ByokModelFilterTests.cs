using System.Reflection;
using System.Text.Json;
using GitHub.Copilot.SDK;
using TroubleScout;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests;

public class ByokModelFilterTests
{
    [Fact]
    public void ParseByokModelsResponse_FiltersDallE3()
    {
        var json = BuildModelsJson(("dall-e-3", null), ("gpt-4o", null));
        var result = InvokeParse(json);
        Assert.DoesNotContain(result.Models, m => m.Id == "dall-e-3");
        Assert.Contains(result.Models, m => m.Id == "gpt-4o");
    }

    [Fact]
    public void ParseByokModelsResponse_FiltersEmbeddingModel()
    {
        var json = BuildModelsJson(("text-embedding-3-large", null), ("gpt-4.1", null));
        var result = InvokeParse(json);
        Assert.DoesNotContain(result.Models, m => m.Id == "text-embedding-3-large");
        Assert.Contains(result.Models, m => m.Id == "gpt-4.1");
    }

    [Fact]
    public void ParseByokModelsResponse_FiltersApiModeImageGeneration()
    {
        var json = BuildModelsJsonWithMode(("custom-image-model", "image_generation"), ("gpt-5", "chat"));
        var result = InvokeParse(json);
        Assert.DoesNotContain(result.Models, m => m.Id == "custom-image-model");
        Assert.Contains(result.Models, m => m.Id == "gpt-5");
    }

    [Fact]
    public void ParseByokModelsResponse_KeepsApiModeResponses()
    {
        var json = BuildModelsJsonWithMode(("gpt-5", "responses"));
        var result = InvokeParse(json);

        Assert.Contains(result.Models, m => m.Id == "gpt-5");
    }

    [Fact]
    public void ParseByokModelsResponse_KeepsUnknownModels()
    {
        var json = BuildModelsJson(("my-custom-finetune", null));
        var result = InvokeParse(json);
        Assert.Contains(result.Models, m => m.Id == "my-custom-finetune");
    }

    [Fact]
    public void ParseByokModelsResponse_FiltersWhisper()
    {
        var json = BuildModelsJson(("whisper-1", null), ("gpt-4o-mini", null));
        var result = InvokeParse(json);
        Assert.DoesNotContain(result.Models, m => m.Id == "whisper-1");
    }

    [Fact]
    public void ParseByokModelsResponse_FiltersGptImage()
    {
        var json = BuildModelsJson(("gpt-image-1", null), ("gpt-5-mini", null));
        var result = InvokeParse(json);
        Assert.DoesNotContain(result.Models, m => m.Id == "gpt-image-1");
        Assert.Contains(result.Models, m => m.Id == "gpt-5-mini");
    }

    [Fact]
    public void ParseByokModelsResponse_FiltersSoraFluxAndRerankFamilies()
    {
        var json = BuildModelsJson(
            ("azure/sora-2", null),
            ("azure_ai/flux.1-kontext-pro", null),
            ("bedrock/cohere.rerank-v3-5:0", null),
            ("bedrock/global.anthropic.claude-sonnet-4-6-v1:0", null));

        var result = InvokeParse(json);

        Assert.DoesNotContain(result.Models, m => m.Id == "azure/sora-2");
        Assert.DoesNotContain(result.Models, m => m.Id == "azure_ai/flux.1-kontext-pro");
        Assert.DoesNotContain(result.Models, m => m.Id == "bedrock/cohere.rerank-v3-5:0");
        Assert.Contains(result.Models, m => m.Id == "bedrock/global.anthropic.claude-sonnet-4-6-v1:0");
    }

    [Fact]
    public void ParseByokModelsResponse_DoesNotFilterChatModelWhenDisplayNameLooksNonChat()
    {
        var json = BuildModelsJson(("custom-chat-model", "Flux Assistant"));
        var result = InvokeParse(json);

        Assert.Contains(result.Models, m => m.Id == "custom-chat-model");
    }

    private static string BuildModelsJson(params (string Id, string? Name)[] models)
    {
        return JsonSerializer.Serialize(new
        {
            data = models.Select(model => new Dictionary<string, object?>
            {
                ["id"] = model.Id,
                ["name"] = model.Name ?? model.Id
            })
        });
    }

    private static string BuildModelsJsonWithMode(params (string Id, string Mode)[] models)
    {
        return JsonSerializer.Serialize(new
        {
            data = models.Select(model => new Dictionary<string, object?>
            {
                ["id"] = model.Id,
                ["mode"] = model.Mode,
                ["name"] = model.Id
            })
        });
    }

    private static (List<ModelInfo> Models, Dictionary<string, TestByokPriceInfo> PricingByModelId) InvokeParse(string json)
    {
        using var document = JsonDocument.Parse(json);

        var method = typeof(ModelDiscoveryManager).GetMethod("ParseByokModelsResponse", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [document.RootElement]);
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
