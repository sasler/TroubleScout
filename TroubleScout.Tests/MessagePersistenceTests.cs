using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests;

public class MessagePersistenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Func<string, bool> _originalClipboard;

    public MessagePersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TroubleScoutTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _originalClipboard = MessagePersistence.ClipboardWriter;
    }

    public void Dispose()
    {
        MessagePersistence.ClipboardWriter = _originalClipboard;
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Save_WritesContent_ToFile()
    {
        var path = Path.Combine(_tempDir, "out.md");
        var result = MessagePersistence.Save(path, "Hello world", allowOverwrite: false, out _);
        Assert.Equal(SaveMessageResult.Success, result);
        Assert.Equal("Hello world", File.ReadAllText(path));
    }

    [Fact]
    public void Save_RejectsMissingPath()
    {
        var result = MessagePersistence.Save("", "content", false, out _);
        Assert.Equal(SaveMessageResult.PathMissing, result);
    }

    [Fact]
    public void Save_RejectsDirectoryTarget()
    {
        var result = MessagePersistence.Save(_tempDir, "content", false, out _);
        Assert.Equal(SaveMessageResult.PathIsDirectory, result);
    }

    [Fact]
    public void Save_RefusesToCreateParentDirectory()
    {
        var path = Path.Combine(_tempDir, "missing-subdir", "out.md");
        var result = MessagePersistence.Save(path, "content", false, out var detail);
        Assert.Equal(SaveMessageResult.ParentDirectoryMissing, result);
        Assert.NotNull(detail);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Save_RefusesOverwriteByDefault()
    {
        var path = Path.Combine(_tempDir, "out.md");
        File.WriteAllText(path, "original");
        var result = MessagePersistence.Save(path, "new", allowOverwrite: false, out _);
        Assert.Equal(SaveMessageResult.FileAlreadyExists, result);
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void Save_OverwritesWhenAllowed()
    {
        var path = Path.Combine(_tempDir, "out.md");
        File.WriteAllText(path, "original");
        var result = MessagePersistence.Save(path, "new", allowOverwrite: true, out _);
        Assert.Equal(SaveMessageResult.Success, result);
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void Save_FailsWhenContentIsEmpty()
    {
        var path = Path.Combine(_tempDir, "out.md");
        var result = MessagePersistence.Save(path, "", false, out _);
        Assert.Equal(SaveMessageResult.NoMessageAvailable, result);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Copy_PassesContentToClipboardWriter()
    {
        string? captured = null;
        MessagePersistence.ClipboardWriter = c => { captured = c; return true; };

        var ok = MessagePersistence.Copy("payload-text", out var detail);

        Assert.True(ok);
        Assert.Null(detail);
        Assert.Equal("payload-text", captured);
    }

    [Fact]
    public void Copy_DoesNotUseSessionExecutor()
    {
        // Sentinel value via injection; we verify the Copy path goes through
        // the injectable writer (i.e., the local pipeline indirection point),
        // not through any session/JEA executor.
        var calls = 0;
        MessagePersistence.ClipboardWriter = _ => { calls++; return true; };
        MessagePersistence.Copy("x", out _);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Copy_FailsCleanlyWhenNoContent()
    {
        var ok = MessagePersistence.Copy(null, out var detail);
        Assert.False(ok);
        Assert.NotNull(detail);
    }

    [Fact]
    public void Copy_FailsCleanlyWhenWriterReturnsFalse()
    {
        MessagePersistence.ClipboardWriter = _ => false;
        var ok = MessagePersistence.Copy("payload", out _);
        Assert.False(ok);
    }
}
