using System.Reflection;
using TroubleScout.Services;
using TroubleScout.UI;
using Xunit;

namespace TroubleScout.Tests;

public class ThemeTests
{
    private static string IsolatedSettingsPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ts-theme-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    private static IDisposable WithSettingsPath(string path)
    {
        var field = typeof(AppSettingsStore).GetField("_settingsPath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var prev = field!.GetValue(null);
        field.SetValue(null, path);
        return new ResetAction(() => field.SetValue(null, prev));
    }

    private sealed class ResetAction : IDisposable
    {
        private readonly Action _action;
        public ResetAction(Action action) { _action = action; }
        public void Dispose() => _action();
    }

    [Fact]
    public void Theme_DefaultsToDark_WhenSettingsAreEmpty()
    {
        var path = IsolatedSettingsPath();
        using var _ = WithSettingsPath(path);

        var settings = AppSettingsStore.Load();

        Assert.Equal("dark", settings.Theme);
    }

    [Fact]
    public void Theme_RoundTrips_MonoValueThroughDisk()
    {
        var path = IsolatedSettingsPath();
        using var _ = WithSettingsPath(path);

        var settings = AppSettingsStore.Load();
        settings.Theme = "mono";
        AppSettingsStore.Save(settings);

        var reloaded = AppSettingsStore.Load();
        Assert.Equal("mono", reloaded.Theme);
    }

    [Fact]
    public void Theme_LightIsNotYetSupported_FallsBackToDark()
    {
        // "light" was advertised in earlier drafts but is currently a no-op
        // that's identical to "dark". Until a real palette/renderer ships,
        // it's deliberately not in the supported list and should normalize
        // to the default rather than silently masquerading as a real theme.
        var path = IsolatedSettingsPath();
        using var _ = WithSettingsPath(path);

        var settings = AppSettingsStore.Load();
        settings.Theme = "light";
        AppSettingsStore.Save(settings);

        var reloaded = AppSettingsStore.Load();
        Assert.Equal("dark", reloaded.Theme);
    }

    [Fact]
    public void Theme_NormalizesUnknownValueToDark()
    {
        var path = IsolatedSettingsPath();
        using var _ = WithSettingsPath(path);

        var settings = AppSettingsStore.Load();
        settings.Theme = "nonsense";
        AppSettingsStore.Save(settings);

        var reloaded = AppSettingsStore.Load();
        Assert.Equal("dark", reloaded.Theme);
    }

    [Fact]
    public void Theme_NormalizesCaseAndWhitespace()
    {
        var path = IsolatedSettingsPath();
        using var _ = WithSettingsPath(path);

        var settings = AppSettingsStore.Load();
        settings.Theme = "  MoNo  ";
        AppSettingsStore.Save(settings);

        var reloaded = AppSettingsStore.Load();
        Assert.Equal("mono", reloaded.Theme);
    }

    [Fact]
    public void StatusBar_InMonoTheme_ContainsNoSpectreColorTags()
    {
        var prevTheme = ConsoleUI.CurrentTheme;
        try
        {
            ConsoleUI.CurrentTheme = "mono";
            var info = new StatusBarInfo("GPT-5", "Copilot", 100, 50, 150, 1, "abc")
            {
                ReasoningEffort = "medium"
            };
            var method = typeof(ConsoleUI).GetMethod("BuildStatusBarLine", BindingFlags.NonPublic | BindingFlags.Static);
            var line = (string)method!.Invoke(null, new object[] { info, 240 })!;

            Assert.NotEmpty(line);
            // No Spectre color tags should appear in mono. Markup.Escape may still emit
            // "[" inside the content but the framework color/style tags must be gone.
            Assert.DoesNotContain("[grey]", line);
            Assert.DoesNotContain("[magenta]", line);
            Assert.DoesNotContain("[blue]", line);
            Assert.DoesNotContain("[cyan]", line);
            Assert.DoesNotContain("[dim]", line);
        }
        finally
        {
            ConsoleUI.CurrentTheme = prevTheme;
        }
    }

    [Fact]
    public void StatusBar_InDarkTheme_StillUsesSpectreColorTags()
    {
        var prevTheme = ConsoleUI.CurrentTheme;
        try
        {
            ConsoleUI.CurrentTheme = "dark";
            var info = new StatusBarInfo("GPT-5", "Copilot", 100, 50, 150, 1, "abc");
            var method = typeof(ConsoleUI).GetMethod("BuildStatusBarLine", BindingFlags.NonPublic | BindingFlags.Static);
            var line = (string)method!.Invoke(null, new object[] { info, 240 })!;

            Assert.Contains("[grey]", line);
        }
        finally
        {
            ConsoleUI.CurrentTheme = prevTheme;
        }
    }
}
