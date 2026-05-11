using System.Text.Json;
using GitHub.Copilot.SDK;

namespace TroubleScout.Services;

internal static class ByokModelDiscoveryService
{
    internal static async Task<List<ModelInfo>> TryGetByokProviderModelsAsync(
        string? apiKey,
        string baseUrl,
        ByokProviderManager byokProviderManager,
        ModelDiscoveryManager modelDiscovery)
    {
        modelDiscovery.ClearByokPricing();

        if (string.IsNullOrWhiteSpace(apiKey) || !LooksLikeUrl(baseUrl))
        {
            return [];
        }

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        var endpointCandidates = byokProviderManager.BuildByokModelEndpointCandidates(baseUrl);
        foreach (var endpoint in endpointCandidates)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var response = await httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var document = await JsonDocument.ParseAsync(stream);

                var discovery = byokProviderManager.ParseByokModelsResponse(document.RootElement);
                if (discovery.Models.Count > 0)
                {
                    foreach (var entry in discovery.PricingByModelId)
                    {
                        modelDiscovery.StoreByokPricing(entry.Key, entry.Value);
                    }

                    return discovery.Models;
                }
            }
            catch
            {
                // Try next candidate endpoint.
            }
        }

        return [];
    }

    internal static bool LooksLikeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                   || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }
}
