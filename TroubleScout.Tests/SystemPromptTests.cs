using System.Reflection;
using FluentAssertions;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests;

[CollectionDefinition("SystemPromptSettings", DisableParallelization = true)]
public class SystemPromptSettingsCollection { }

[Collection("SystemPromptSettings")]
public class SystemPromptTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string? _originalSettingsPath;

    public SystemPromptTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"troublescout-systemprompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _originalSettingsPath = AppSettingsStore.SettingsPath;
        AppSettingsStore.SettingsPath = Path.Combine(_testDirectory, "settings.json");
    }

    public void Dispose()
    {
        if (_originalSettingsPath != null)
        {
            AppSettingsStore.SettingsPath = _originalSettingsPath;
        }

        try
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DefaultSystemPrompt_ContainsInvestigationApproach()
    {
        await using var session = new TroubleshootingSession("localhost");

        var content = InvokeCreateSystemMessageContent(session, "localhost");

        content.Should().Contain("exhaust ALL available diagnostic tools");
    }

    [Fact]
    public async Task DefaultSystemPrompt_ContainsAutonomousGuidance()
    {
        await using var session = new TroubleshootingSession("localhost");

        var content = InvokeCreateSystemMessageContent(session, "localhost");

        content.Should().Contain("Do NOT pause to ask the user");
    }

    [Fact]
    public async Task SystemPromptOverride_ReplacesSection()
    {
        AppSettingsStore.Save(new AppSettings
        {
            SystemPromptOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["investigation_approach"] = """
                ## Investigation Approach
                - Use the custom investigation workflow.
                """
            }
        });

        await using var session = new TroubleshootingSession("localhost");

        var content = InvokeCreateSystemMessageContent(session, "localhost");

        content.Should().Contain("Use the custom investigation workflow.");
        content.Should().NotContain("exhaust ALL available diagnostic tools");
    }

    [Fact]
    public async Task SystemPromptAppend_AddsToEnd()
    {
        const string appendText = """
            ## Custom Prompt Notes
            - Add this after the built-in guidance.
            """;

        AppSettingsStore.Save(new AppSettings
        {
            SystemPromptAppend = appendText
        });

        await using var session = new TroubleshootingSession("localhost");

        var content = InvokeCreateSystemMessageContent(session, "localhost");

        content.TrimEnd().Should().EndWith(appendText);
    }

    private static string InvokeCreateSystemMessageContent(TroubleshootingSession session, string targetServer)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("CreateSystemMessage", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull("CreateSystemMessage should exist on TroubleshootingSession");

        var config = method!.Invoke(session, [targetServer, null]);
        var content = config?.GetType().GetProperty("Content")?.GetValue(config) as string;

        content.Should().NotBeNullOrWhiteSpace();
        return content!;
    }
}
