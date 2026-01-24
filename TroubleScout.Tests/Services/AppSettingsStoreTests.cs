using FluentAssertions;
using TroubleScout.Services;
using TroubleScout.Tests.Fixtures;
using Xunit;

namespace TroubleScout.Tests.Services;

public class AppSettingsStoreTests : IDisposable
{
    private readonly string _testDirectory;

    public AppSettingsStoreTests()
    {
        _testDirectory = TestHelpers.CreateTempDirectory();
        
        // Set test settings path using the new property
        var testSettingsPath = Path.Combine(_testDirectory, "settings.json");
        AppSettingsStore.SettingsPath = testSettingsPath;
    }

    public void Dispose()
    {
        // Clean up test directory
        TestHelpers.CleanupTempDirectory(_testDirectory);
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
    public void Save_WhenDirectoryDoesNotExist_ShouldCreateIt()
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
}
