using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

public class ModelPricingDatabaseTests
{
    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("claude-sonnet-4.5")]
    [InlineData("gemini-2.5-pro")]
    public void TryGetPrice_KnownModel_ReturnsTrue(string modelId)
    {
        var found = ModelPricingDatabase.TryGetPrice(modelId, out _, out _);

        Assert.True(found);
    }

    [Fact]
    public void TryGetPrice_UnknownModel_ReturnsFalse()
    {
        var found = ModelPricingDatabase.TryGetPrice("some-random-model", out _, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryGetPrice_WithProviderPrefix_StripsPrefix()
    {
        var found = ModelPricingDatabase.TryGetPrice("openai/gpt-4o", out var inputPrice, out var outputPrice);

        Assert.True(found);
        Assert.Equal(2.50m, inputPrice);
        Assert.Equal(10.00m, outputPrice);
    }

    [Fact]
    public void TryGetPrice_DateSuffix_MatchesBase()
    {
        var found = ModelPricingDatabase.TryGetPrice("gpt-4o-2024-08-06", out var inputPrice, out var outputPrice);

        Assert.True(found);
        Assert.Equal(2.50m, inputPrice);
        Assert.Equal(10.00m, outputPrice);
    }

    [Fact]
    public void TryGetPrice_BedrockAnthropicVariant_MatchesCanonicalModel()
    {
        var found = ModelPricingDatabase.TryGetPrice("bedrock/global.anthropic.claude-sonnet-4-6-v1:0", out var inputPrice, out var outputPrice);

        Assert.True(found);
        Assert.Equal(3.00m, inputPrice);
        Assert.Equal(15.00m, outputPrice);
    }

    [Fact]
    public void TryGetPrice_BedrockNovaVariant_MatchesCanonicalModel()
    {
        var found = ModelPricingDatabase.TryGetPrice("bedrock/amazon.nova-pro-v1:0", out var inputPrice, out var outputPrice);

        Assert.True(found);
        Assert.Equal(0.80m, inputPrice);
        Assert.Equal(3.20m, outputPrice);
    }

    [Fact]
    public void TryGetPrice_DeepSeekReasoner_UsesR1Pricing()
    {
        var found = ModelPricingDatabase.TryGetPrice("deepseek-reasoner", out var inputPrice, out var outputPrice);

        Assert.True(found);
        Assert.Equal(0.55m, inputPrice);
        Assert.Equal(2.19m, outputPrice);
    }

    [Fact]
    public void TryGetPrice_DeepSeekChat_UsesV3Pricing()
    {
        var found = ModelPricingDatabase.TryGetPrice("deepseek-chat", out var inputPrice, out var outputPrice);

        Assert.True(found);
        Assert.Equal(0.27m, inputPrice);
        Assert.Equal(1.10m, outputPrice);
    }

    [Fact]
    public void TryGetMode_ChatModel_ReturnsChat()
    {
        var found = ModelPricingDatabase.TryGetMode("gpt-4o", out var mode);

        Assert.True(found);
        Assert.Equal("chat", mode);
    }

    [Fact]
    public void TryGetMode_ImageModel_ReturnsImageGeneration()
    {
        var found = ModelPricingDatabase.TryGetMode("dall-e-3", out var mode);

        Assert.True(found);
        Assert.Equal("image_generation", mode);
    }

    [Fact]
    public void IsNonChatModel_ImageModel_ReturnsTrue()
    {
        var isNonChat = ModelPricingDatabase.IsNonChatModel("dall-e-3");

        Assert.True(isNonChat);
    }

    [Fact]
    public void IsNonChatModel_ChatModel_ReturnsFalse()
    {
        var isNonChat = ModelPricingDatabase.IsNonChatModel("gpt-4o");

        Assert.False(isNonChat);
    }

    [Fact]
    public void IsNonChatModel_UnknownModel_ReturnsFalse()
    {
        var isNonChat = ModelPricingDatabase.IsNonChatModel("some-random-model");

        Assert.False(isNonChat);
    }

    [Theory]
    [InlineData("azure/sora-2")]
    [InlineData("azure_ai/flux.1-pro")]
    [InlineData("bedrock/cohere.rerank-v3-5:0")]
    public void IsNonChatModel_NonChatProviderVariant_ReturnsTrue(string modelId)
    {
        var isNonChat = ModelPricingDatabase.IsNonChatModel(modelId);

        Assert.True(isNonChat);
    }

    [Fact]
    public void TryGetPrice_ReturnsCorrectValues()
    {
        var found = ModelPricingDatabase.TryGetPrice("gpt-4o", out var inputPrice, out var outputPrice);

        Assert.True(found);
        Assert.Equal(2.50m, inputPrice);
        Assert.Equal(10.00m, outputPrice);
    }
}
