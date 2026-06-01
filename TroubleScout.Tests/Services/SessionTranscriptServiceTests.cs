using FluentAssertions;
using System.Text.Json;
using TroubleScout.Services;
using TroubleScout.UI;
using Xunit;

namespace TroubleScout.Tests.Services;

public sealed class SessionTranscriptServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"troublescout-transcript-tests-{Guid.NewGuid():N}");

    public SessionTranscriptServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Save_ShouldRejectEmptyHistory()
    {
        var path = Path.Combine(_tempDir, "session.json");

        var result = SessionTranscriptService.Save(path, [], summary: null, allowOverwrite: false, out var detail);

        result.Should().Be(SessionTranscriptSaveResult.NoHistory);
        detail.Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Save_ShouldRejectDirectoryTarget()
    {
        var result = SessionTranscriptService.Save(_tempDir, [CreatePrompt("hello")], summary: null, allowOverwrite: false, out _);

        result.Should().Be(SessionTranscriptSaveResult.PathIsDirectory);
    }

    [Fact]
    public void Save_ShouldRejectMissingParentDirectory()
    {
        var path = Path.Combine(_tempDir, "missing", "session.json");

        var result = SessionTranscriptService.Save(path, [CreatePrompt("hello")], summary: null, allowOverwrite: false, out var detail);

        result.Should().Be(SessionTranscriptSaveResult.ParentDirectoryMissing);
        detail.Should().Be(Path.GetDirectoryName(Path.GetFullPath(path)));
    }

    [Fact]
    public void Save_ShouldRejectExistingFileUnlessOverwriteAllowed()
    {
        var path = Path.Combine(_tempDir, "session.json");
        File.WriteAllText(path, "{}");

        var result = SessionTranscriptService.Save(path, [CreatePrompt("hello")], summary: null, allowOverwrite: false, out _);

        result.Should().Be(SessionTranscriptSaveResult.FileAlreadyExists);
    }

    [Fact]
    public void Save_ShouldRedactTranscriptContentBeforeWriting()
    {
        const string secret = "abcdef1234567890";
        var path = Path.Combine(_tempDir, "session.json");
        var prompt = CreatePrompt($"Investigate api_key={secret}") with
        {
            AgentReply = $"Use token={secret} to reproduce.",
            StatusBar = new StatusBarInfo(
                Model: $"model client_secret={secret}",
                Provider: $"provider access_token={secret}",
                InputTokens: 1,
                OutputTokens: 2,
                TotalTokens: 3,
                ToolInvocations: 1,
                SessionId: "session")
            {
                ReasoningEffort = $"high token={secret}",
                SessionCostEstimate = $"cost api_key={secret}"
            }
        };
        prompt.Actions.Add(new ReportActionEntry(
            DateTimeOffset.UtcNow,
            "localhost",
            $"Get-Thing -Token {secret}",
            $"Authorization: Bearer {secret}",
            "SafeAuto",
            "PowerShell")
        {
            Arguments = $"{{\"api_key\":\"{secret}\"}}",
            ToolCallId = $"call token={secret}"
        });

        var result = SessionTranscriptService.Save(path, [prompt], summary: null, allowOverwrite: false, out _);

        result.Should().Be(SessionTranscriptSaveResult.Success);
        var json = File.ReadAllText(path);
        json.Should().NotContain(secret);
        json.Should().Contain(SecretRedactor.Mask);
    }

    [Fact]
    public void Load_ShouldRejectMissingFile()
    {
        var result = SessionTranscriptService.Load(Path.Combine(_tempDir, "missing.json"), out var transcript, out _);

        result.Should().Be(SessionTranscriptLoadResult.FileNotFound);
        transcript.Should().BeNull();
    }

    [Fact]
    public void Load_ShouldRejectDirectoryTarget()
    {
        var result = SessionTranscriptService.Load(_tempDir, out var transcript, out _);

        result.Should().Be(SessionTranscriptLoadResult.PathIsDirectory);
        transcript.Should().BeNull();
    }

    [Fact]
    public void Load_ShouldRejectMalformedJson()
    {
        var path = Path.Combine(_tempDir, "bad.json");
        File.WriteAllText(path, "{not-json");

        var result = SessionTranscriptService.Load(path, out var transcript, out _);

        result.Should().Be(SessionTranscriptLoadResult.MalformedJson);
        transcript.Should().BeNull();
    }

    [Fact]
    public void Load_ShouldRejectUnsupportedSchemaVersion()
    {
        var path = Path.Combine(_tempDir, "future.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 999,
          "createdAt": "2026-05-09T12:00:00Z",
          "prompts": [
            {
              "timestamp": "2026-05-09T12:00:00Z",
              "prompt": "hello",
              "actions": [],
              "agentReply": "reply"
            }
          ]
        }
        """);

        var result = SessionTranscriptService.Load(path, out var transcript, out var detail);

        result.Should().Be(SessionTranscriptLoadResult.UnsupportedSchemaVersion);
        transcript.Should().BeNull();
        detail.Should().Contain("999");
    }

    [Fact]
    public void Load_ShouldRejectEmptyTranscript()
    {
        var path = Path.Combine(_tempDir, "empty.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "createdAt": "2026-05-09T12:00:00Z",
          "prompts": []
        }
        """);

        var result = SessionTranscriptService.Load(path, out var transcript, out _);

        result.Should().Be(SessionTranscriptLoadResult.EmptyTranscript);
        transcript.Should().BeNull();
    }

    [Fact]
    public void Load_ShouldAcceptLegacySchemaVersionOne()
    {
        var path = Path.Combine(_tempDir, "legacy.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "createdAt": "2026-05-09T12:00:00Z",
          "prompts": [
            {
              "timestamp": "2026-05-09T12:00:00Z",
              "prompt": "hello",
              "actions": [],
              "agentReply": "reply"
            }
          ]
        }
        """);

        var result = SessionTranscriptService.Load(path, out var transcript, out _);

        result.Should().Be(SessionTranscriptLoadResult.Success);
        transcript!.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public void SaveAndLoad_ShouldRoundTripPromptActionsStatusBarAndSummary()
    {
        var path = Path.Combine(_tempDir, "session.json");
        var summary = CreateSummary();
        var prompt = CreatePrompt("Check services") with
        {
            Reasoning = "Checked service state before answering.",
            StatusBar = new StatusBarInfo("gpt-5", "GitHub Copilot", 10, 20, 30, 1, "session-1")
            {
                ReasoningEffort = "high",
                SessionInputTokens = 10,
                SessionOutputTokens = 20,
                SessionCostEstimate = "$0.01"
            }
        };
        prompt.Actions.Add(new ReportActionEntry(
            DateTimeOffset.Parse("2026-05-09T12:01:00Z"),
            "localhost",
            "Get-Service",
            "Spooler Running",
            "SafeAuto",
            "PowerShell")
        {
            Arguments = "{\"name\":\"Spooler\"}",
            Success = true,
            ToolCallId = "tool-1"
        });

        SessionTranscriptService.Save(path, [prompt], summary, allowOverwrite: false, out _)
            .Should().Be(SessionTranscriptSaveResult.Success);

        var result = SessionTranscriptService.Load(path, out var transcript, out _);

        result.Should().Be(SessionTranscriptLoadResult.Success);
        transcript.Should().NotBeNull();
        transcript!.SchemaVersion.Should().Be(SessionTranscriptService.CurrentSchemaVersion);
        transcript.Prompts.Should().ContainSingle();
        transcript.Prompts[0].Prompt.Should().Be("Check services");
        transcript.Prompts[0].AgentReply.Should().Be("No obvious failures.");
        transcript.Prompts[0].Reasoning.Should().Be("Checked service state before answering.");
        transcript.Prompts[0].Actions.Should().ContainSingle();
        transcript.Prompts[0].Actions[0].Command.Should().Be("Get-Service");
        transcript.Prompts[0].StatusBar!.Model.Should().Be("gpt-5");
        transcript.Summary!.CurrentModel.Should().Be("gpt-5");
        transcript.Summary.AgentModels.Should().ContainKey("subagent").WhoseValue.Should().Be("gpt-4.1");
        transcript.Summary.GitHubBillingDisplayMode.Should().Be("ai-credits");
        transcript.Summary.SubagentCalls.Should().Be(2);
        transcript.Summary.SubagentTokens.Should().Be(44);
    }

    [Fact]
    public void SavedJson_ShouldUseVersionedCamelCaseSchema()
    {
        var path = Path.Combine(_tempDir, "session.json");

        SessionTranscriptService.Save(path, [CreatePrompt("hello")], CreateSummary(), allowOverwrite: false, out _)
            .Should().Be(SessionTranscriptSaveResult.Success);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        root.GetProperty("schemaVersion").GetInt32().Should().Be(SessionTranscriptService.CurrentSchemaVersion);
        root.GetProperty("createdAt").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.Now, TimeSpan.FromSeconds(5));
        root.GetProperty("summary").GetProperty("currentModel").GetString().Should().Be("gpt-5");
        root.GetProperty("prompts").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void Save_ShouldRedactReasoningBeforeWriting()
    {
        const string secret = "abcdef1234567890";
        var path = Path.Combine(_tempDir, "session.json");
        var prompt = CreatePrompt("hello") with
        {
            Reasoning = $"Inspected token={secret} before answering."
        };

        SessionTranscriptService.Save(path, [prompt], summary: null, allowOverwrite: false, out _)
            .Should().Be(SessionTranscriptSaveResult.Success);

        var json = File.ReadAllText(path);
        json.Should().NotContain(secret);
        json.Should().Contain(SecretRedactor.Mask);
    }

    private static ReportPromptEntry CreatePrompt(string prompt) =>
        new(DateTimeOffset.Parse("2026-05-09T12:00:00Z"), prompt, [], "No obvious failures.");

    private static ReportSessionSummary CreateSummary() =>
        new(
            CurrentModel: "gpt-5",
            CurrentProvider: "GitHub Copilot",
            ModelsUsed: ["gpt-5"],
            ConfiguredMcpServers: [],
            UsedMcpServers: [],
            MonitoringMcp: null,
            TicketingMcp: null,
            ApprovedMcpServersForSession: [],
            PersistedApprovedMcpServers: [],
            ConfiguredSkills: [],
            UsedSkills: [],
            ExecutionMode: "Strict",
            TargetServer: "localhost")
        {
            AgentModels = new Dictionary<string, string> { ["subagent"] = "gpt-4.1" },
            GitHubBillingDisplayMode = "ai-credits",
            SubagentCalls = 2,
            SubagentTokens = 44
        };
}
