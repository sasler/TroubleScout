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
    public List<string>? SafeCommands { get; set; }
}

public static class AppSettingsStore
{
    private static string? _settingsPath;
    internal static readonly string[] DefaultSafeCommands =
    [
        "Get-*",
        "Select-*",
        "Sort-*",
        "Group-*",
        "Where-*",
        "ForEach-*",
        "Measure-*",
        "Test-*",
        "ConvertTo-*",
        "ConvertFrom-*",
        "Compare-*",
        "Find-*",
        "Search-*",
        "Resolve-*",
        "Out-String",
        "Out-Null",
        "Format-Custom",
        "Format-Hex",
        "Format-List",
        "Format-Table",
        "Format-Wide"
    ];
    
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
            {
                var defaultSettings = CreateNormalizedSettings(new AppSettings(), out _);
                Save(defaultSettings);
                return defaultSettings;
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

            if (string.IsNullOrWhiteSpace(settings.ByokOpenAiApiKey)
                && !string.IsNullOrWhiteSpace(settings.ByokOpenAiApiKeyEncrypted))
            {
                settings.ByokOpenAiApiKey = TryDecrypt(settings.ByokOpenAiApiKeyEncrypted);
            }

            settings = CreateNormalizedSettings(settings, out var changed);
            if (changed)
            {
                Save(settings);
            }

            return settings;
        }
        catch
        {
            return CreateNormalizedSettings(new AppSettings(), out _);
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
            ByokOpenAiApiKeyEncrypted = TryEncrypt(settings.ByokOpenAiApiKey),
            SafeCommands = settings.SafeCommands?.Where(command => !string.IsNullOrWhiteSpace(command)).Select(command => command.Trim()).ToList()
                ?? DefaultSafeCommands.ToList()
        };

        var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(SettingsPath, json);
    }

    private static AppSettings CreateNormalizedSettings(AppSettings settings, out bool changed)
    {
        changed = false;

        if (settings.SafeCommands == null)
        {
            settings.SafeCommands = DefaultSafeCommands.ToList();
            changed = true;
        }

        return settings;
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
