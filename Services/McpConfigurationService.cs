using System.Text.Json;
using GitHub.Copilot.SDK;

namespace TroubleScout.Services;

internal sealed record McpConfigurationLoadResult(
    IReadOnlyDictionary<string, McpServerConfig> Servers,
    IReadOnlyList<string> Warnings);

internal static class McpConfigurationService
{
    internal static McpConfigurationLoadResult LoadServers(string? mcpConfigPath)
    {
        var warnings = new List<string>();
        var servers = LoadServers(mcpConfigPath, warnings);
        return new McpConfigurationLoadResult(servers, warnings);
    }

    internal static Dictionary<string, McpServerConfig> LoadServers(string? mcpConfigPath, ICollection<string> warnings)
    {
        var result = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            return result;
        }

        if (!File.Exists(mcpConfigPath))
        {
            warnings.Add($"MCP config file not found: {mcpConfigPath}");
            return result;
        }

        try
        {
            using var stream = File.OpenRead(mcpConfigPath);
            using var document = JsonDocument.Parse(stream);

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("MCP config root must be a JSON object.");
                return result;
            }

            JsonElement serversElement;
            if (!root.TryGetProperty("mcpServers", out serversElement) &&
                !root.TryGetProperty("servers", out serversElement))
            {
                warnings.Add("MCP config does not contain 'mcpServers' or 'servers'.");
                return result;
            }

            if (serversElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("MCP config 'mcpServers' or 'servers' must be a JSON object.");
                return result;
            }

            foreach (var property in serversElement.EnumerateObject())
            {
                var mapped = TryMapMcpServer(property.Name, property.Value, out var warning);
                if (mapped == null)
                {
                    if (!string.IsNullOrWhiteSpace(warning))
                    {
                        warnings.Add(warning);
                    }

                    continue;
                }

                result[property.Name] = mapped;
            }
        }
        catch (JsonException ex)
        {
            warnings.Add($"MCP config JSON parse error: {TrimSingleLine(ex.Message)}");
        }
        catch (Exception ex)
        {
            warnings.Add($"MCP config load failed: {TrimSingleLine(ex.Message)}");
        }

        return result;
    }

    private static McpServerConfig? TryMapMcpServer(string serverName, JsonElement serverElement, out string? warning)
    {
        warning = null;

        if (serverElement.ValueKind != JsonValueKind.Object)
        {
            warning = $"Skipping MCP server '{serverName}': entry must be an object.";
            return null;
        }

        var type = GetOptionalString(serverElement, "type")?.Trim().ToLowerInvariant();
        if (type is "http" or "sse" or "remote")
        {
            var url = GetOptionalString(serverElement, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                warning = $"Skipping MCP server '{serverName}': remote server requires 'url'.";
                return null;
            }

            var remote = new McpHttpServerConfig
            {
                Url = url!
            };

            var headers = GetStringDictionary(serverElement, "headers");
            if (headers != null)
            {
                remote.Headers = headers;
            }

            var remoteTools = GetStringList(serverElement, "tools");
            if (remoteTools != null)
            {
                remote.Tools = remoteTools;
            }

            var remoteTimeout = GetOptionalInt(serverElement, "timeout");
            if (remoteTimeout.HasValue)
            {
                remote.Timeout = remoteTimeout.Value;
            }

            return remote;
        }

        var command = GetOptionalString(serverElement, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            warning = $"Skipping MCP server '{serverName}': local/stdio server requires 'command'.";
            return null;
        }

        var local = new McpStdioServerConfig
        {
            Command = command!
        };

        var args = GetStringList(serverElement, "args");
        if (args != null)
        {
            local.Args = args;
        }

        var env = GetStringDictionary(serverElement, "env");
        if (env != null)
        {
            local.Env = env;
        }

        var cwd = GetOptionalString(serverElement, "cwd");
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            local.Cwd = cwd;
        }

        var localTools = GetStringList(serverElement, "tools");
        if (localTools != null)
        {
            local.Tools = localTools;
        }

        var localTimeout = GetOptionalInt(serverElement, "timeout");
        if (localTimeout.HasValue)
        {
            local.Timeout = localTimeout.Value;
        }

        return local;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetInt32(out var value) ? value : null;
    }

    private static List<string>? GetStringList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                list.Add(value);
            }
        }

        return list.Count == 0 ? null : list;
    }

    private static Dictionary<string, string>? GetStringDictionary(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in property.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                dict[item.Name] = value!;
            }
        }

        return dict.Count == 0 ? null : dict;
    }

    private static string TrimSingleLine(string value)
        => value.ReplaceLineEndings(" ").Trim();
}
