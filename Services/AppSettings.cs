using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TroubleScout.Services;

public sealed class AppSettings
{
    public string? LastModel { get; set; }
    public bool UseByokOpenAi { get; set; }
    public string? ByokOpenAiBaseUrl { get; set; }
    public string? ByokOpenAiApiKey { get; set; }
    public string? ByokOpenAiApiKeyEncrypted { get; set; }
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
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

            if (string.IsNullOrWhiteSpace(settings.ByokOpenAiApiKey)
                && !string.IsNullOrWhiteSpace(settings.ByokOpenAiApiKeyEncrypted))
            {
                settings.ByokOpenAiApiKey = TryDecrypt(settings.ByokOpenAiApiKeyEncrypted);
            }

            return settings;
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

        var persisted = new AppSettings
        {
            LastModel = settings.LastModel,
            UseByokOpenAi = settings.UseByokOpenAi,
            ByokOpenAiBaseUrl = settings.ByokOpenAiBaseUrl,
            ByokOpenAiApiKey = null,
            ByokOpenAiApiKeyEncrypted = TryEncrypt(settings.ByokOpenAiApiKey)
        };

        var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(SettingsPath, json);
    }

    private static string? TryEncrypt(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            return plainText;
        }

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return plainText;
        }
    }

    private static string? TryDecrypt(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            return cipherText;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(cipherText);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return cipherText;
        }
    }
}
