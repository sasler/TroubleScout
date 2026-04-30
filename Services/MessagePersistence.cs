namespace TroubleScout.Services;

/// <summary>
/// Outcome of a /save attempt. Pure value — formatted by the caller for the TUI.
/// </summary>
public enum SaveMessageResult
{
    Success,
    NoMessageAvailable,
    PathMissing,
    PathIsDirectory,
    ParentDirectoryMissing,
    FileAlreadyExists,
    WriteFailed
}

/// <summary>
/// Pure helpers for /save and /copy used by the interactive loop. Side effects
/// are intentionally limited to file I/O for /save, and clipboard delegation for
/// /copy. /copy must not use the active session executor (which targets the
/// remote server / JEA endpoint).
/// </summary>
public static class MessagePersistence
{
    /// <summary>
    /// Persist a captured assistant message to <paramref name="path"/>. Strict by
    /// design: refuses directories, refuses to create parent directories, and
    /// requires <paramref name="allowOverwrite"/> to be true if the target file
    /// already exists. Returns a <see cref="SaveMessageResult"/> describing the
    /// outcome; the caller is responsible for user-facing messaging.
    /// </summary>
    public static SaveMessageResult Save(
        string? path,
        string? content,
        bool allowOverwrite,
        out string? errorDetail)
    {
        errorDetail = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return SaveMessageResult.PathMissing;
        }

        if (string.IsNullOrEmpty(content))
        {
            return SaveMessageResult.NoMessageAvailable;
        }

        var fullPath = Path.GetFullPath(path);

        if (Directory.Exists(fullPath))
        {
            return SaveMessageResult.PathIsDirectory;
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            errorDetail = parent;
            return SaveMessageResult.ParentDirectoryMissing;
        }

        if (File.Exists(fullPath) && !allowOverwrite)
        {
            return SaveMessageResult.FileAlreadyExists;
        }

        try
        {
            File.WriteAllText(fullPath, content);
            return SaveMessageResult.Success;
        }
        catch (Exception ex)
        {
            errorDetail = ex.Message;
            return SaveMessageResult.WriteFailed;
        }
    }

    /// <summary>
    /// Indirection point for clipboard writes. Tests override this to avoid
    /// touching the real clipboard. The default implementation runs a
    /// dedicated short-lived local PowerShell pipeline (NOT the session
    /// executor, which targets the remote/JEA host). The return value is
    /// true on success, false if no clipboard service is available.
    /// </summary>
    public static Func<string, bool> ClipboardWriter { get; set; } = DefaultClipboardWriter;

    private static bool DefaultClipboardWriter(string content)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.AddCommand("Set-Clipboard").AddParameter("Value", content);
            ps.Invoke();
            return !ps.HadErrors;
        }
        catch
        {
            return false;
        }
    }

    public static bool Copy(string? content, out string? errorDetail)
    {
        errorDetail = null;
        if (string.IsNullOrEmpty(content))
        {
            errorDetail = "No assistant message available yet.";
            return false;
        }

        try
        {
            return ClipboardWriter(content);
        }
        catch (Exception ex)
        {
            errorDetail = ex.Message;
            return false;
        }
    }
}
