using GitHub.Copilot.SDK;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests;

public class ModelDiscoveryCacheTests
{
    [Fact]
    public async Task GetMergedModelListAsync_FirstCall_InvokesFetcher()
    {
        var manager = new ModelDiscoveryManager();
        var fetchCount = 0;

        var result = await manager.GetMergedModelListAsync("cli-path-1", () =>
        {
            fetchCount++;
            return Task.FromResult(new List<ModelInfo> { new() { Id = "gpt-5", Name = "GPT-5" } });
        });

        Assert.Equal(1, fetchCount);
        Assert.Single(result);
        Assert.Equal("gpt-5", result[0].Id);
    }

    [Fact]
    public async Task GetMergedModelListAsync_SecondCall_UsesCachedValueWithoutFetching()
    {
        var manager = new ModelDiscoveryManager();
        var fetchCount = 0;
        Func<Task<List<ModelInfo>>> fetcher = () =>
        {
            fetchCount++;
            return Task.FromResult(new List<ModelInfo> { new() { Id = "gpt-5", Name = "GPT-5" } });
        };

        await manager.GetMergedModelListAsync("cli-path-1", fetcher);
        var second = await manager.GetMergedModelListAsync("cli-path-1", fetcher);

        Assert.Equal(1, fetchCount);
        Assert.Single(second);
        Assert.Equal("gpt-5", second[0].Id);
    }

    [Fact]
    public async Task GetMergedModelListAsync_DifferentCacheKey_RefetchesAndUpdatesCache()
    {
        var manager = new ModelDiscoveryManager();
        var fetchCount = 0;
        Func<Task<List<ModelInfo>>> fetcher = () =>
        {
            fetchCount++;
            return Task.FromResult(new List<ModelInfo>
            {
                new() { Id = $"model-{fetchCount}", Name = $"Model {fetchCount}" }
            });
        };

        var first = await manager.GetMergedModelListAsync("cli-path-A", fetcher);
        var second = await manager.GetMergedModelListAsync("cli-path-B", fetcher);
        var third = await manager.GetMergedModelListAsync("cli-path-B", fetcher);

        Assert.Equal(2, fetchCount);
        Assert.Equal("model-1", first[0].Id);
        Assert.Equal("model-2", second[0].Id);
        Assert.Equal("model-2", third[0].Id);
    }

    [Fact]
    public async Task InvalidateMergedModelListCache_ForcesNextCallToRefetch()
    {
        var manager = new ModelDiscoveryManager();
        var fetchCount = 0;
        Func<Task<List<ModelInfo>>> fetcher = () =>
        {
            fetchCount++;
            return Task.FromResult(new List<ModelInfo>
            {
                new() { Id = $"v{fetchCount}", Name = $"Version {fetchCount}" }
            });
        };

        await manager.GetMergedModelListAsync("cli-path-1", fetcher);
        manager.InvalidateMergedModelListCache();
        var afterInvalidate = await manager.GetMergedModelListAsync("cli-path-1", fetcher);

        Assert.Equal(2, fetchCount);
        Assert.Equal("v2", afterInvalidate[0].Id);
    }

    [Fact]
    public async Task GetMergedModelListAsync_ReturnsDefensiveCopy_MutationDoesNotAffectCache()
    {
        var manager = new ModelDiscoveryManager();
        var fetchCount = 0;
        Func<Task<List<ModelInfo>>> fetcher = () =>
        {
            fetchCount++;
            return Task.FromResult(new List<ModelInfo>
            {
                new() { Id = "gpt-5", Name = "GPT-5" }
            });
        };

        var first = await manager.GetMergedModelListAsync("cli-path-1", fetcher);
        first.Clear();
        first.Add(new ModelInfo { Id = "tampered", Name = "Tampered" });

        var second = await manager.GetMergedModelListAsync("cli-path-1", fetcher);

        Assert.Equal(1, fetchCount);
        Assert.Single(second);
        Assert.Equal("gpt-5", second[0].Id);
    }

    [Fact]
    public async Task GetMergedModelListAsync_EmptyResult_StillCaches()
    {
        var manager = new ModelDiscoveryManager();
        var fetchCount = 0;
        Func<Task<List<ModelInfo>>> fetcher = () =>
        {
            fetchCount++;
            return Task.FromResult(new List<ModelInfo>());
        };

        await manager.GetMergedModelListAsync("cli-path-1", fetcher);
        await manager.GetMergedModelListAsync("cli-path-1", fetcher);

        Assert.Equal(1, fetchCount);
    }

    [Fact]
    public async Task InvalidateMergedModelListCache_BeforeFirstCall_IsNoop()
    {
        var manager = new ModelDiscoveryManager();

        manager.InvalidateMergedModelListCache();
        var fetchCount = 0;
        var result = await manager.GetMergedModelListAsync("cli", () =>
        {
            fetchCount++;
            return Task.FromResult(new List<ModelInfo> { new() { Id = "x", Name = "X" } });
        });

        Assert.Equal(1, fetchCount);
        Assert.Single(result);
    }
}
