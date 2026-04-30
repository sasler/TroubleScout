using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using TroubleScout;
using Xunit;

namespace TroubleScout.Tests;

/// <summary>
/// Source- and reflection-level guards for invariants that are easy to break
/// during refactors but hard to catch with conventional behavioral tests.
///
/// AGENTS.md calls these out explicitly: per-server MCP approval, JEA must
/// never use AddScript, IncludeSubAgentStreamingEvents stays false, the
/// PauseForApproval/ResumeAfterApproval pairing around prompts, and the
/// cancellation token reaching CopilotSession.SendAsync.
/// </summary>
public class InvariantGuardsTests
{
    private static readonly Lazy<string> RepoRootPath = new(FindRepoRoot);

    [Fact]
    public void PowerShellExecutor_AddScript_IsGuardedByIsJeaSessionCheck()
    {
        // JEA endpoints are commonly NoLanguage. AddScript() is therefore
        // forbidden on the JEA path; commands must be built via AddCommand().
        // This pins the guard so a refactor cannot accidentally let the
        // non-JEA branch reach AddScript before checking IsJeaSession.

        var source = ReadRepoFile("Services", "PowerShellExecutor.cs");
        var lines = source.Split('\n');

        var addScriptLines = new List<int>();
        var jeaCheckLines = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"\bps\.AddScript\("))
            {
                addScriptLines.Add(i);
            }
            if (Regex.IsMatch(lines[i], @"\bif\s*\(\s*IsJeaSession\s*\)"))
            {
                jeaCheckLines.Add(i);
            }
        }

        addScriptLines.Should().NotBeEmpty(
            "the executor still uses AddScript on the non-JEA path; if that changed " +
            "this guard needs to be revisited.");
        jeaCheckLines.Should().NotBeEmpty(
            "the JEA short-circuit must remain present in PowerShellExecutor.");

        foreach (var addScriptLine in addScriptLines)
        {
            jeaCheckLines.Should().Contain(j => j < addScriptLine,
                $"AddScript at line {addScriptLine + 1} must be preceded by an `if (IsJeaSession)` " +
                "branch that diverts JEA execution to the no-language pipeline.");
        }
    }

    [Fact]
    public void PowerShellExecutor_ConfigureJeaPipeline_DoesNotCallAddScript()
    {
        // ConfigureJeaPipeline is the JEA branch entry point; it must build
        // commands via AddCommand/AddParameter only.
        var source = ReadRepoFile("Services", "PowerShellExecutor.cs");
        var lines = source.Split('\n');

        var startIdx = Array.FindIndex(lines, l =>
            Regex.IsMatch(l, @"\bConfigureJeaPipeline\s*\(\s*PowerShell\s+ps"));
        startIdx.Should().BeGreaterOrEqualTo(0,
            "ConfigureJeaPipeline must exist on the JEA execution path.");

        var depth = 0;
        var seenOpenBrace = false;
        var sliceLines = new List<string>();
        for (var i = startIdx; i < lines.Length; i++)
        {
            sliceLines.Add(lines[i]);
            foreach (var ch in lines[i])
            {
                if (ch == '{') { depth++; seenOpenBrace = true; }
                else if (ch == '}') { depth--; }
            }
            if (seenOpenBrace && depth == 0) break;
        }

        var slice = string.Join('\n', sliceLines);
        Regex.IsMatch(slice, @"\bAddScript\(").Should().BeFalse(
            "ConfigureJeaPipeline must build commands via AddCommand/AddParameter, " +
            "not AddScript — JEA endpoints commonly run in NoLanguage mode.");
    }

    [Fact]
    public void TroubleshootingSession_SendAsync_PassesCancellationToken()
    {
        // ESC cancellation must reach the SDK so the in-flight RPC actually
        // aborts — not just the UI. The call site in SendMessageAsync passes
        // the cancellation token straight to CopilotSession.SendAsync.
        var source = ReadRepoFile("TroubleshootingSession.cs");
        source.Should().MatchRegex(
            @"_copilotSession\.SendAsync\([^;]*cancellationToken[^;]*\);",
            "the cancellation token must flow into CopilotSession.SendAsync so ESC " +
            "cancellation reaches the SDK, not just the TUI.");
    }

    [Fact]
    public void TroubleshootingSession_PublicSendMessageAsync_AcceptsCancellationToken()
    {
        var sendMethod = typeof(TroubleshootingSession)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(TroubleshootingSession.SendMessageAsync))
            .ToList();

        sendMethod.Should().NotBeEmpty();
        sendMethod.Should().Contain(m =>
            m.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken)),
            "the public SendMessageAsync entry point must accept a CancellationToken " +
            "so callers (including the ESC poller) can cancel an in-flight turn.");
    }

    [Fact]
    public void ConsoleUI_ApprovalPrompts_AllPauseAndResumeLiveIndicator()
    {
        // Every approval/selection prompt is invoked while the LiveThinkingIndicator
        // may be running. Without PauseForApproval/ResumeAfterApproval the spinner
        // keeps writing over the prompt and the user can't see (or answer) it.
        var source = ReadRepoFile("UI", "ConsoleUI.cs");

        var promptMethodNames = new[]
        {
            "PromptCommandApproval",
            "PromptUrlApproval",
            "PromptMcpApproval",
            "PromptPostAnalysisAction",
            "PromptMcpRoleSelection",
            "PromptBatchApproval",
        };

        foreach (var methodName in promptMethodNames)
        {
            var body = ExtractMethodBody(source, methodName);
            body.Should().NotBeNullOrEmpty(
                $"{methodName} must exist; rename the test if the method was renamed.");
            body.Should().Contain("PauseForApproval()",
                $"{methodName} must call LiveThinkingIndicator.PauseForApproval() before prompting.");
            body.Should().Contain("ResumeAfterApproval()",
                $"{methodName} must call LiveThinkingIndicator.ResumeAfterApproval() in a finally block.");
            body.Should().Contain("finally",
                $"{methodName} must restore the live indicator in a finally block so " +
                "exceptions don't leave the spinner paused.");
        }
    }

    [Fact]
    public void TroubleshootingSession_KeepsSubAgentStreamingDisabled()
    {
        // Re-asserting the AGENTS.md rule from a different angle than the
        // existing assertion in TroubleshootingSessionTests so neither test
        // can silently drift.
        var source = ReadRepoFile("TroubleshootingSession.cs");
        source.Should().MatchRegex(
            @"IncludeSubAgentStreamingEvents\s*=\s*false",
            "AGENTS.md requires IncludeSubAgentStreamingEvents to stay false until the " +
            "TUI is updated to render sub-agent deltas separately.");
        source.Should().NotMatchRegex(
            @"IncludeSubAgentStreamingEvents\s*=\s*true",
            "Setting IncludeSubAgentStreamingEvents = true requires a coordinated TUI " +
            "change; do not enable it without updating the rendering pipeline.");
    }

    [Fact]
    public void ConsoleUI_McpApprovalResult_HasPersistOption()
    {
        // Per-server MCP approval with a "persist across sessions" tier is an
        // explicit AGENTS.md invariant. Removing or renaming this enum value
        // would silently drop the persistent approval path.
        var enumType = Type.GetType("TroubleScout.UI.McpApprovalResult, TroubleScout")
                       ?? AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(a => SafeGetTypes(a))
                            .FirstOrDefault(t => t.Name == "McpApprovalResult");

        enumType.Should().NotBeNull("McpApprovalResult enum must exist for MCP approval prompts.");
        enumType!.IsEnum.Should().BeTrue();

        var names = Enum.GetNames(enumType);
        names.Should().Contain("ApproveOnce");
        names.Should().Contain("ApproveServerForSession");
        names.Should().Contain("ApproveServerPersist");
        names.Should().Contain("Deny");
    }

    [Fact]
    public void AppSettings_ExposesPersistedApprovedMcpServers()
    {
        // Persisted approvals are stored on AppSettings and loaded at startup.
        // Renaming the property would silently drop persisted trust on next load.
        var prop = typeof(TroubleScout.Services.AppSettings)
            .GetProperty("PersistedApprovedMcpServers");
        prop.Should().NotBeNull(
            "AppSettings.PersistedApprovedMcpServers is the storage key for cross-session " +
            "MCP approvals; do not rename without a migration.");
        prop!.PropertyType.Should().Be(typeof(List<string>),
            "PersistedApprovedMcpServers must remain a nullable list of server names.");
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }

    private static string ReadRepoFile(params string[] relativeSegments)
    {
        var path = Path.Combine(new[] { RepoRootPath.Value }.Concat(relativeSegments).ToArray());
        File.Exists(path).Should().BeTrue($"expected source file at {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TroubleScout.sln"))
                || File.Exists(Path.Combine(dir.FullName, "TroubleScout.csproj")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate TroubleScout repo root by walking up from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Extracts the textual body of the named method from a C# source file by
    /// finding the method header and matching braces. Comments and strings are
    /// not parsed precisely; this is sufficient for grep-style guard checks and
    /// is more robust than a single regex against multi-line method bodies.
    /// </summary>
    internal static string ExtractMethodBody(string source, string methodName)
    {
        // Match an access modifier on the declaration line, then anything (including
        // tuple return types with parens), then the method name and its open paren.
        var headerRegex = new Regex(
            @"(public|internal|private|protected)[^\r\n]*?\b"
            + Regex.Escape(methodName) + @"\s*\(",
            RegexOptions.Compiled);
        var match = headerRegex.Match(source);
        if (!match.Success) return string.Empty;

        var i = match.Index;
        // Walk to the opening brace that starts the body.
        while (i < source.Length && source[i] != '{') i++;
        if (i >= source.Length) return string.Empty;

        var depth = 0;
        var start = i;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(start, i - start + 1);
                }
            }
        }
        return string.Empty;
    }
}
