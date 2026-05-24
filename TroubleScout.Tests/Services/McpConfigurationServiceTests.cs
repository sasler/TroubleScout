using FluentAssertions;
using GitHub.Copilot;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

public class McpConfigurationServiceTests
{
    [Fact]
    public void LoadServers_WhenFileMissing_ShouldReturnEmptyAndWarning()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        var result = McpConfigurationService.LoadServers(path);

        result.Servers.Should().BeEmpty();
        result.Warnings.Should().ContainSingle().Which.Should().Contain("not found");
    }

    [Fact]
    public void LoadServers_WhenPathNotProvided_ShouldReturnEmptyWithoutWarning()
    {
        var result = McpConfigurationService.LoadServers(null);

        result.Servers.Should().BeEmpty();
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void LoadServers_WithValidJson_ShouldParseRemoteAndLocalServers()
    {
        using var temp = TemporaryDirectory.Create();
        var filePath = Path.Combine(temp.Path, "mcp-config.json");
        File.WriteAllText(filePath, """
        {
            "mcpServers": {
                "remote-server": {
                    "type": "http",
                    "url": "https://example.com/mcp",
                    "headers": {
                        "Authorization": "Bearer secret"
                    },
                    "tools": ["*"],
                    "timeout": 30
                },
                "local-server": {
                    "type": "local",
                    "command": "node",
                    "args": ["server.js"],
                    "env": {
                        "TOKEN": "secret"
                    },
                    "cwd": "C:\\tools",
                    "timeout": 10
                }
            }
        }
        """);

        var result = McpConfigurationService.LoadServers(filePath);

        result.Warnings.Should().BeEmpty();
        result.Servers.Should().HaveCount(2);
        result.Servers.Keys.Should().Contain(["remote-server", "local-server"]);
        result.Servers["remote-server"].Should().BeOfType<McpHttpServerConfig>();
        result.Servers["local-server"].Should().BeOfType<McpStdioServerConfig>();
    }

    [Fact]
    public void LoadServers_WithSseRemoteServer_ShouldParseRemoteUrl()
    {
        using var temp = TemporaryDirectory.Create();
        var filePath = Path.Combine(temp.Path, "mcp-config.json");
        File.WriteAllText(filePath, """
        {
            "mcpServers": {
                "sse-server": {
                    "type": "sse",
                    "url": "https://example.com/sse"
                }
            }
        }
        """);

        var result = McpConfigurationService.LoadServers(filePath);

        result.Warnings.Should().BeEmpty();
        var server = result.Servers["sse-server"].Should().BeOfType<McpHttpServerConfig>().Subject;
        server.Url.Should().Be("https://example.com/sse");
    }

    [Fact]
    public void LoadServers_WithServersProperty_ShouldParseServerEntries()
    {
        using var temp = TemporaryDirectory.Create();
        var filePath = Path.Combine(temp.Path, "mcp-config.json");
        File.WriteAllText(filePath, """
        {
            "servers": {
                "context7": {
                    "command": "npx",
                    "args": ["-y", "@upstash/context7-mcp"]
                }
            }
        }
        """);

        var result = McpConfigurationService.LoadServers(filePath);

        result.Warnings.Should().BeEmpty();
        result.Servers.Should().ContainKey("context7");
        result.Servers["context7"].Should().BeOfType<McpStdioServerConfig>();
    }

    [Fact]
    public void LoadServers_WithInvalidEntry_ShouldSkipOnlyInvalidServer()
    {
        using var temp = TemporaryDirectory.Create();
        var filePath = Path.Combine(temp.Path, "mcp-config.json");
        File.WriteAllText(filePath, """
        {
            "mcpServers": {
                "invalid-server": {
                    "type": "http"
                },
                "valid-server": {
                    "type": "http",
                    "url": "https://example.com/mcp"
                }
            }
        }
        """);

        var result = McpConfigurationService.LoadServers(filePath);

        result.Servers.Should().ContainSingle();
        result.Servers.Keys.Should().Contain("valid-server");
        result.Warnings.Should().ContainSingle().Which.Should().Contain("invalid-server");
    }

    [Fact]
    public void LoadServers_WithMalformedJson_ShouldReturnParseWarningWithoutSecretValues()
    {
        using var temp = TemporaryDirectory.Create();
        var filePath = Path.Combine(temp.Path, "mcp-config.json");
        File.WriteAllText(filePath, """{ "mcpServers": { "secret-server": { "command": """);

        var result = McpConfigurationService.LoadServers(filePath);

        result.Servers.Should().BeEmpty();
        result.Warnings.Should().ContainSingle().Which.Should().Contain("JSON parse error");
        result.Warnings[0].Should().NotContain("secret-server");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"troublescout-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
