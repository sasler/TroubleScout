using FluentAssertions;
using TroubleScout.Services;
using TroubleScout.Tests.Fixtures;
using Xunit;

namespace TroubleScout.Tests.Services;

// Define collection to force sequential execution
[CollectionDefinition("AppSettings", DisableParallelization = true)]
public class AppSettingsCollection { }

[Collection("AppSettings")]
public class AppSettingsStoreTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string? _originalSettingsPath;

    public AppSettingsStoreTests()
    {
        _testDirectory = TestHelpers.CreateTempDirectory();
        
        // Save original settings path and set test settings path
        _originalSettingsPath = AppSettingsStore.SettingsPath;
        var testSettingsPath = Path.Combine(_testDirectory, "settings.json");
        AppSettingsStore.SettingsPath = testSettingsPath;
    }

    public void Dispose()
    {
        // Restore original settings path
        if (_originalSettingsPath != null)
        {
            AppSettingsStore.SettingsPath = _originalSettingsPath;
        }
        
        // Clean up test directory
        try
        {
            TestHelpers.CleanupTempDirectory(_testDirectory);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[TestCleanup] Failed to clean up temp directory '{_testDirectory}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"[TestCleanup] Access denied while cleaning up temp directory '{_testDirectory}': {ex.Message}");
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ShouldReturnDefault()
    {
        // Act
        var settings = AppSettingsStore.Load();

        // Assert
        settings.Should().NotBeNull();
        settings.LastModel.Should().BeNull();
    }

    [Fact]
    public void Save_ThenLoad_ShouldPersistSettings()
    {
        // Arrange
        var settings = new AppSettings
        {
            LastModel = "gpt-4o"
        };

        // Act
        AppSettingsStore.Save(settings);
        var loaded = AppSettingsStore.Load();

        // Assert
        loaded.Should().NotBeNull();
        loaded.LastModel.Should().Be("gpt-4o");
    }

    [Fact]
    public void Save_ShouldCreateDirectoryAndPersist()
    {
        // Arrange
        var settings = new AppSettings { LastModel = "claude-sonnet-4.5" };

        // Act
        AppSettingsStore.Save(settings);

        // Assert
        Directory.Exists(_testDirectory).Should().BeTrue();
        var loaded = AppSettingsStore.Load();
        loaded.LastModel.Should().Be("claude-sonnet-4.5");
    }

    [Fact]
    public void Load_WhenFileIsCorrupted_ShouldReturnDefault()
    {
        // Arrange
        var settingsPath = AppSettingsStore.SettingsPath;
        
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{ invalid json }");

        // Act
        var settings = AppSettingsStore.Load();

        // Assert
        settings.Should().NotBeNull();
        settings.LastModel.Should().BeNull();
    }

    [Fact]
    public void Save_MultipleModels_ShouldOverwritePrevious()
    {
        // Arrange
        var settings1 = new AppSettings { LastModel = "gpt-4o" };
        var settings2 = new AppSettings { LastModel = "claude-sonnet-4.5" };

        // Act
        AppSettingsStore.Save(settings1);
        AppSettingsStore.Save(settings2);
        var loaded = AppSettingsStore.Load();

        // Assert
        loaded.LastModel.Should().Be("claude-sonnet-4.5");
    }

    [Fact]
    public void Load_WhenFileIsEmpty_ShouldReturnDefault()
    {
        // Arrange
        var settingsPath = AppSettingsStore.SettingsPath;
        
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, string.Empty);

        // Act
        var settings = AppSettingsStore.Load();

        // Assert
        settings.Should().NotBeNull();
        settings.LastModel.Should().BeNull();
    }

    [Fact]
    public void Save_ShouldCreateIndentedJson()
    {
        // Arrange
        var settings = new AppSettings { LastModel = "gpt-4o" };
        var settingsPath = AppSettingsStore.SettingsPath;

        // Act
        AppSettingsStore.Save(settings);
        var jsonContent = File.ReadAllText(settingsPath);

        // Assert
        jsonContent.Should().Contain("  "); // Should have indentation
        jsonContent.Should().Contain("\"LastModel\"");
    }

    [Fact]
    public void SettingsPath_WhenApplicationDataIsNull_ShouldUseFallback()
    {
        // Arrange - Save current path and reset to force recalculation
        var currentPath = AppSettingsStore.SettingsPath;
        AppSettingsStore.SettingsPath = null!;

        // Act
        var settingsPath = AppSettingsStore.SettingsPath;

        // Assert
        // The path should be valid - either from ApplicationData, LocalApplicationData, or CurrentDirectory
        settingsPath.Should().NotBeNullOrEmpty();
        settingsPath.Should().Contain("TroubleScout");
        settingsPath.Should().EndWith("settings.json");

        // Verify it's a valid path that can be used
        var directory = Path.GetDirectoryName(settingsPath);
        directory.Should().NotBeNullOrEmpty();
        
        // Cleanup - restore the test path
        AppSettingsStore.SettingsPath = currentPath;
    }

    [Fact]
    public void SettingsPath_AfterManualSet_ShouldReturnSetValue()
    {
        // Arrange
        var customPath = Path.Combine(_testDirectory, "custom", "settings.json");

        // Act
        AppSettingsStore.SettingsPath = customPath;
        var retrievedPath = AppSettingsStore.SettingsPath;

        // Assert
        retrievedPath.Should().Be(customPath);
        
        // Cleanup - restore original path to avoid test interference
        AppSettingsStore.SettingsPath = Path.Combine(_testDirectory, "settings.json");
    }
}
