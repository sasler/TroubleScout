using System.Text.Json;

namespace TroubleScout.Services;

public sealed class AppSettings
{
    public string? LastModel { get; set; }
    public bool UseByokOpenAi { get; set; }
    public string? ByokOpenAiBaseUrl { get; set; }
    public string? ByokOpenAiApiKey { get; set; }
}

public static class AppSettingsStore
{
    private static string? _settingsPath;
    
    public static string SettingsPath
    {
        get
        {
            if (_settingsPath != null)
                return _settingsPath;

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
            {
                // Fallback to LocalApplicationData if ApplicationData is unavailable
                appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(appData))
                {
                    // Final fallback to current directory if no application data folders are available
                    appData = Environment.CurrentDirectory;
                }
            }

            _settingsPath = Path.Combine(appData, "TroubleScout", "settings.json");
            return _settingsPath;
        }
        internal set => _settingsPath = value;
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(SettingsPath, json);
    }
}
