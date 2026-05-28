using System.Security.Cryptography;
using System.Text.Json;

namespace TroubleScout.Services;

internal sealed record DelegatedPowerShellScript(
    string ScriptId,
    string Path,
    string Script,
    string Description,
    string? SessionName,
    DateTimeOffset CreatedAt);

public sealed class DelegatedPowerShellScriptStore
{
    private sealed record ScriptMetadata(string Description, string? SessionName, DateTimeOffset CreatedAt);

    private readonly string _rootDirectory;
    private readonly Dictionary<string, DelegatedPowerShellScript> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    internal DelegatedPowerShellScriptStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TroubleScout", "scripts");
    }

    internal DelegatedPowerShellScript Stage(string script, string description, string? sessionName)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new ArgumentException("Script cannot be empty.", nameof(script));
        }

        Directory.CreateDirectory(_rootDirectory);
        CleanupStaleScripts(TimeSpan.FromHours(4));

        var scriptId = CreateScriptId();
        var path = System.IO.Path.Combine(_rootDirectory, $"{scriptId}.ps1");
        var metadataPath = System.IO.Path.Combine(_rootDirectory, $"{scriptId}.json");
        var fullRoot = System.IO.Path.GetFullPath(_rootDirectory);
        var fullPath = System.IO.Path.GetFullPath(path);
        var fullMetadataPath = System.IO.Path.GetFullPath(metadataPath);
        if (!IsPathWithinDirectory(fullPath, fullRoot) || !IsPathWithinDirectory(fullMetadataPath, fullRoot))
        {
            throw new InvalidOperationException("Script path escaped the TroubleScout temp script directory.");
        }

        var normalizedDescription = string.IsNullOrWhiteSpace(description) ? "Delegated PowerShell script" : description.Trim();
        var normalizedSessionName = string.IsNullOrWhiteSpace(sessionName) ? null : sessionName.Trim();
        var createdAt = DateTimeOffset.Now;
        File.WriteAllText(fullPath, script);
        File.WriteAllText(
            fullMetadataPath,
            JsonSerializer.Serialize(new ScriptMetadata(normalizedDescription, normalizedSessionName, createdAt)));
        var staged = new DelegatedPowerShellScript(
            scriptId,
            fullPath,
            script,
            normalizedDescription,
            normalizedSessionName,
            createdAt);

        lock (_lock)
        {
            _scripts[scriptId] = staged;
        }

        return staged;
    }

    internal bool TryGet(string scriptId, out DelegatedPowerShellScript script)
    {
        script = null!;
        if (string.IsNullOrWhiteSpace(scriptId) || scriptId.Any(ch => !Uri.IsHexDigit(ch)))
        {
            return false;
        }

        lock (_lock)
        {
            if (_scripts.TryGetValue(scriptId, out script!))
            {
                return true;
            }
        }

        var path = System.IO.Path.Combine(_rootDirectory, $"{scriptId}.ps1");
        var fullRoot = System.IO.Path.GetFullPath(_rootDirectory);
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!IsPathWithinDirectory(fullPath, fullRoot) || !File.Exists(fullPath))
        {
            return false;
        }

        var content = File.ReadAllText(fullPath);
        var metadata = ReadMetadata(scriptId, fullRoot);
        script = new DelegatedPowerShellScript(
            scriptId,
            fullPath,
            content,
            metadata?.Description ?? "Delegated PowerShell script",
            metadata?.SessionName,
            metadata?.CreatedAt ?? File.GetCreationTimeUtc(fullPath));
        return true;
    }

    internal void Delete(string scriptId)
    {
        DelegatedPowerShellScript? script = null;
        lock (_lock)
        {
            if (_scripts.Remove(scriptId, out var staged))
            {
                script = staged;
            }
        }

        if (script != null)
        {
            TryDeleteFile(script.Path);
            TryDeleteFile(GetMetadataPath(script.ScriptId));
            return;
        }

        if (!string.IsNullOrWhiteSpace(scriptId) && scriptId.All(Uri.IsHexDigit))
        {
            TryDeleteFile(System.IO.Path.Combine(_rootDirectory, $"{scriptId}.ps1"));
            TryDeleteFile(GetMetadataPath(scriptId));
        }
    }

    private void CleanupStaleScripts(TimeSpan maxAge)
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return;
        }

        var cutoff = DateTimeOffset.Now - maxAge;
        foreach (var path in Directory.EnumerateFiles(_rootDirectory, "*.ps1"))
        {
            try
            {
                if (File.GetCreationTimeUtc(path) < cutoff.UtcDateTime)
                {
                    File.Delete(path);
                    TryDeleteFile(System.IO.Path.ChangeExtension(path, ".json"));
                }
            }
            catch
            {
                // Best-effort temp cleanup must not block troubleshooting.
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; report history keeps the script text.
        }
    }

    private ScriptMetadata? ReadMetadata(string scriptId, string fullRoot)
    {
        var metadataPath = GetMetadataPath(scriptId);
        var fullMetadataPath = System.IO.Path.GetFullPath(metadataPath);
        if (!IsPathWithinDirectory(fullMetadataPath, fullRoot) || !File.Exists(fullMetadataPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ScriptMetadata>(File.ReadAllText(fullMetadataPath));
        }
        catch
        {
            return null;
        }
    }

    private string GetMetadataPath(string scriptId)
        => System.IO.Path.Combine(_rootDirectory, $"{scriptId}.json");

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        var normalizedDirectory = directory.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? directory
            : directory + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateScriptId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
