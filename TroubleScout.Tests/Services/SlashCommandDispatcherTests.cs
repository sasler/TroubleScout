using FluentAssertions;
using GitHub.Copilot.SDK;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

public class SlashCommandDispatcherTests
{
    [Fact]
    public void SlashCommands_ShouldComeFromRegistrySuggestions()
    {
        SlashCommandDispatcher.SlashCommands.Should().Equal(SlashCommandRegistry.SlashCommands);
    }

    [Theory]
    [InlineData("/mode", "/mode", true)]
    [InlineData("/mode safe", "/mode", true)]
    [InlineData("/modeX", "/mode", false)]
    [InlineData("/server srv01", "/server", true)]
    [InlineData("/serverX", "/server", false)]
    public void IsInvocation_ShouldMatchOnlyExactCommandOrCommandWithArguments(string input, string command, bool expected)
    {
        SlashCommandDispatcher.IsInvocation(input, command).Should().Be(expected);
    }

    [Fact]
    public void Dispatch_WithUnknownSlashCommand_ShouldFallThrough()
    {
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers());

        var result = dispatcher.Dispatch("/does-not-exist");

        result.Handled.Should().BeFalse();
        result.ExitRequested.Should().BeFalse();
    }

    [Fact]
    public void Dispatch_WithHelp_ShouldHandleKnownCommand()
    {
        var calls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            ShowHelp = () => calls++
        });

        var result = dispatcher.Dispatch("/help");

        result.Handled.Should().BeTrue();
        result.ExitRequested.Should().BeFalse();
        calls.Should().Be(1);
    }

    [Theory]
    [InlineData("/exit")]
    [InlineData("/quit")]
    [InlineData("exit")]
    [InlineData("quit")]
    public void Dispatch_WithExitCommand_ShouldRequestExit(string input)
    {
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers());

        var result = dispatcher.Dispatch(input);

        result.Handled.Should().BeTrue();
        result.ExitRequested.Should().BeTrue();
    }

    [Fact]
    public void Dispatch_WithModeWithoutArgument_ShouldShowCurrentModeAndUsage()
    {
        var messages = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetExecutionMode = () => ExecutionMode.Safe,
            ShowInfo = messages.Add
        });

        var result = dispatcher.Dispatch("/mode");

        result.Handled.Should().BeTrue();
        messages.Should().Contain("Current mode: safe");
        messages.Should().Contain("Usage: /mode <safe|yolo>");
    }

    [Fact]
    public void Dispatch_WithInvalidMode_ShouldWarnAndNotSetMode()
    {
        var warnings = new List<string>();
        var setCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            ShowWarning = warnings.Add,
            SetExecutionMode = _ => setCalls++
        });

        var result = dispatcher.Dispatch("/mode maybe");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("Invalid mode. Use: safe or yolo.");
        setCalls.Should().Be(0);
    }

    [Fact]
    public void Dispatch_WithModeArgument_ShouldSetModeAndShowStatus()
    {
        ExecutionMode? mode = null;
        var statusCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            SetExecutionMode = value => mode = value,
            SetConsoleExecutionMode = value => mode = value,
            ShowStatus = _ => statusCalls++
        });

        var result = dispatcher.Dispatch("/mode yolo");

        result.Handled.Should().BeTrue();
        mode.Should().Be(ExecutionMode.Yolo);
        statusCalls.Should().Be(1);
    }

    [Fact]
    public void Dispatch_WithThemeArgument_ShouldNormalizePersistAndWarnForUnknownTheme()
    {
        string? appliedTheme = null;
        string? persistedTheme = null;
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            SetTheme = value => appliedTheme = value,
            PersistTheme = value => persistedTheme = value,
            ShowWarning = warnings.Add
        });

        var result = dispatcher.Dispatch("/theme neon");

        result.Handled.Should().BeTrue();
        appliedTheme.Should().Be("dark");
        persistedTheme.Should().Be("dark");
        warnings.Should().Contain("Unknown theme 'neon'. Falling back to 'dark'. Supported: dark, mono.");
    }

    [Fact]
    public void Dispatch_WithSaveAndNoMessage_ShouldShowNoMessageWarning()
    {
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetLastAssistantMessage = () => null,
            ShowWarning = warnings.Add
        });

        var result = dispatcher.Dispatch("/save out.md");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain(message => message.Contains("No assistant message captured yet", StringComparison.Ordinal));
    }

    [Fact]
    public void Dispatch_WithCopyAndNoMessage_ShouldShowNoMessageWarning()
    {
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetLastAssistantMessage = () => null,
            ShowWarning = warnings.Add
        });

        var result = dispatcher.Dispatch("/copy");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain(message => message.Contains("No assistant message captured yet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchAsync_WithReportAndNoHistory_ShouldShowNoHistoryMessage()
    {
        var messages = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetRecordedPrompts = () => [],
            ShowInfo = messages.Add
        });

        var result = await dispatcher.DispatchAsync("/report");

        result.Handled.Should().BeTrue();
        result.ExitRequested.Should().BeFalse();
        messages.Should().Contain("No prompts recorded yet. Ask a question first, then run /report.");
    }

    [Fact]
    public async Task DispatchAsync_WithReportAndHistory_ShouldWriteReportHtmlWithSummary()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"troublescout-dispatcher-{Guid.NewGuid():N}.html");
        string? writtenPath = null;
        string? writtenHtml = null;
        var summary = new ReportSessionSummary(
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
            ExecutionMode: "safe",
            TargetServer: "localhost");
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetRecordedPrompts = () =>
            [
                new ReportPromptEntry(DateTimeOffset.UtcNow, "Check services", [], "Looks healthy.")
            ],
            GetReportSessionSummary = () => summary,
            CreateReportPath = () => reportPath,
            WriteReportHtml = (path, html) =>
            {
                writtenPath = path;
                writtenHtml = html;
            },
            OpenReport = _ => { }
        });

        var result = await dispatcher.DispatchAsync("/report");

        result.Handled.Should().BeTrue();
        writtenPath.Should().Be(reportPath);
        writtenHtml.Should().Contain("Check services");
        writtenHtml.Should().Contain("gpt-5");
    }

    [Fact]
    public async Task DispatchAsync_WithReportOpenSuccess_ShouldShowSuccessAndReportDirectory()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"troublescout-dispatcher-{Guid.NewGuid():N}");
        var reportPath = Path.Combine(reportDir, "report.html");
        var successes = new List<string>();
        var messages = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetRecordedPrompts = () =>
            [
                new ReportPromptEntry(DateTimeOffset.UtcNow, "Check services", [], "Looks healthy.")
            ],
            CreateReportPath = () => reportPath,
            WriteReportHtml = (_, _) => { },
            OpenReport = _ => { },
            ShowSuccess = successes.Add,
            ShowInfo = messages.Add
        });

        var result = await dispatcher.DispatchAsync("/report");

        result.Handled.Should().BeTrue();
        successes.Should().Contain($"Report generated and opened: {reportPath}");
        messages.Should().Contain($"Reports are stored in temp: {reportDir}");
    }

    [Fact]
    public async Task DispatchAsync_WithReportOpenFailure_ShouldStillWriteAndWarn()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"troublescout-dispatcher-{Guid.NewGuid():N}.html");
        var writeCalls = 0;
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetRecordedPrompts = () =>
            [
                new ReportPromptEntry(DateTimeOffset.UtcNow, "Check services", [], "Looks healthy.")
            ],
            CreateReportPath = () => reportPath,
            WriteReportHtml = (_, _) => writeCalls++,
            OpenReport = _ => throw new InvalidOperationException("browser failed\r\nsecond line"),
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/report");

        result.Handled.Should().BeTrue();
        writeCalls.Should().Be(1);
        warnings.Should().Contain($"Report generated at {reportPath}, but could not auto-open browser: browser failed");
    }

    [Fact]
    public async Task DispatchAsync_WithReportLikeUnknownCommand_ShouldFallThrough()
    {
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers());

        var result = await dispatcher.DispatchAsync("/reportx");

        result.Handled.Should().BeFalse();
        result.ExitRequested.Should().BeFalse();
    }

    [Fact]
    public async Task DispatchAsync_WithMcpApprovalsAndNoApprovals_ShouldShowEmptyState()
    {
        var messages = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetApprovedMcpServers = () => [],
            GetPersistedApprovedMcpServers = () => [],
            ShowInfo = messages.Add
        });

        var result = await dispatcher.DispatchAsync("/mcp-approvals");

        result.Handled.Should().BeTrue();
        result.ExitRequested.Should().BeFalse();
        messages.Should().Equal(
            "No MCP approvals are active for this session.",
            "MCP servers you approve via the prompt appear here automatically.");
    }

    [Fact]
    public async Task DispatchAsync_WithMcpApprovalsList_ShouldShowSortedActiveApprovalsWithRoleAndPersistedFlags()
    {
        var messages = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetApprovedMcpServers = () => ["zabbix", "context7", "redmine"],
            GetPersistedApprovedMcpServers = () => ["Redmine"],
            GetMcpServerRole = server => server.Equals("zabbix", StringComparison.OrdinalIgnoreCase)
                ? "monitoring"
                : server.Equals("redmine", StringComparison.OrdinalIgnoreCase)
                    ? "ticketing"
                    : null,
            ShowInfo = messages.Add
        });

        var result = await dispatcher.DispatchAsync("/mcp-approvals list");

        result.Handled.Should().BeTrue();
        messages.Should().Equal(
            "Active MCP approvals (3):",
            "  context7",
            "  redmine [ticketing] [persisted]",
            "  zabbix [monitoring]",
            "Use /mcp-approvals clear all  or  /mcp-approvals clear <server> to remove persisted approvals.");
    }

    [Fact]
    public async Task DispatchAsync_WithMcpApprovalsList_ShouldShowPersistedApprovalsThatAreNotCurrentlyActive()
    {
        var messages = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetApprovedMcpServers = () => ["zabbix"],
            GetPersistedApprovedMcpServers = () => ["redmine", "zabbix"],
            ShowInfo = messages.Add
        });

        var result = await dispatcher.DispatchAsync("/mcp-approvals list");

        result.Handled.Should().BeTrue();
        messages.Should().Equal(
            "Active MCP approvals (1):",
            "  zabbix [persisted]",
            "Persisted but not currently active:",
            "  redmine",
            "Use /mcp-approvals clear all  or  /mcp-approvals clear <server> to remove persisted approvals.");
    }

    [Theory]
    [InlineData(0, "Cleared session MCP approvals (no persisted approvals were stored).")]
    [InlineData(1, "Cleared 1 persisted MCP approval and reset session approvals.")]
    [InlineData(2, "Cleared 2 persisted MCP approvals and reset session approvals.")]
    public async Task DispatchAsync_WithMcpApprovalsClearAll_ShouldClearPersistedAndSessionApprovals(
        int persistedRemoved,
        string expectedMessage)
    {
        var clearSessionCalls = 0;
        var successes = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            ClearPersistedMcpApprovals = () => persistedRemoved,
            ClearSessionMcpApprovals = () => clearSessionCalls++,
            ShowSuccess = successes.Add
        });

        var result = await dispatcher.DispatchAsync("/mcp-approvals clear all");

        result.Handled.Should().BeTrue();
        clearSessionCalls.Should().Be(1);
        successes.Should().ContainSingle().Which.Should().Be(expectedMessage);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DispatchAsync_WithMcpApprovalsClearServer_ShouldSucceedWhenPersistedOrSessionApprovalRemoved(
        bool persistedRemoved,
        bool sessionRemoved)
    {
        string? persistedTarget = null;
        string? sessionTarget = null;
        var successes = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            RemovePersistedMcpApproval = target =>
            {
                persistedTarget = target;
                return persistedRemoved;
            },
            RemoveSessionMcpApproval = target =>
            {
                sessionTarget = target;
                return sessionRemoved;
            },
            ShowSuccess = successes.Add
        });

        var result = await dispatcher.DispatchAsync("/mcp-approvals clear redmine");

        result.Handled.Should().BeTrue();
        persistedTarget.Should().Be("redmine");
        sessionTarget.Should().Be("redmine");
        successes.Should().Contain("Removed MCP approval for 'redmine'.");
    }

    [Fact]
    public async Task DispatchAsync_WithMcpApprovalsClearServer_ShouldWarnWhenNoApprovalRemoved()
    {
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            RemovePersistedMcpApproval = _ => false,
            RemoveSessionMcpApproval = _ => false,
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/mcp-approvals clear redmine");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("No active MCP approval found for 'redmine'.");
    }

    [Theory]
    [InlineData("/mcp-approvals clear", "Use /mcp-approvals clear all  or  /mcp-approvals clear <server>.")]
    [InlineData("/mcp-approvals show", "Use /mcp-approvals [list|clear all|clear <server>].")]
    public async Task DispatchAsync_WithInvalidMcpApprovalsCommand_ShouldShowUsageWarning(string input, string expectedWarning)
    {
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync(input);

        result.Handled.Should().BeTrue();
        warnings.Should().Contain(expectedWarning);
    }

    [Fact]
    public async Task DispatchAsync_WithMcpApprovalsLikeUnknownCommand_ShouldFallThrough()
    {
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers());

        var result = await dispatcher.DispatchAsync("/mcp-approvalsx");

        result.Handled.Should().BeFalse();
        result.ExitRequested.Should().BeFalse();
    }

    [Fact]
    public void CreateDefaultReportPath_ShouldUseTroubleScoutTempReportsDirectory()
    {
        var reportPath = SlashCommandDispatcher.CreateDefaultReportPath();
        var reportsDir = Path.Combine(Path.GetTempPath(), "TroubleScout", "reports");

        reportPath.Should().StartWith(reportsDir + Path.DirectorySeparatorChar);
        Path.GetFileName(reportPath).Should().MatchRegex(@"^troublescout-report-\d{8}-\d{6}\.html$");
        Directory.Exists(reportsDir).Should().BeTrue();
    }

    [Fact]
    public void WriteReportHtmlFile_ShouldWriteUtf8Html()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"troublescout-dispatcher-{Guid.NewGuid():N}.html");
        const string html = "<html><body>Report \u2713</body></html>";

        try
        {
            SlashCommandDispatcher.WriteReportHtmlFile(reportPath, html);

            File.ReadAllText(reportPath, System.Text.Encoding.UTF8).Should().Be(html);
        }
        finally
        {
            if (File.Exists(reportPath))
            {
                File.Delete(reportPath);
            }
        }
    }

    [Fact]
    public void CreateReportOpenStartInfo_ShouldUseCmdStartWithoutShellExecute()
    {
        const string reportPath = @"C:\Temp\TroubleScout\reports\report.html";

        var psi = SlashCommandDispatcher.CreateReportOpenStartInfo(reportPath);

        psi.FileName.Should().Be("cmd.exe");
        psi.Arguments.Should().Be(@"/c start """" ""C:\Temp\TroubleScout\reports\report.html""");
        psi.UseShellExecute.Should().BeFalse();
        psi.CreateNoWindow.Should().BeTrue();
    }

    [Fact]
    public void Dispatch_WithTranscriptSave_ShouldWriteCurrentHistory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"troublescout-dispatcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "session.json");
        var successes = new List<string>();
        try
        {
            var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
            {
                GetRecordedPrompts = () =>
                [
                    new ReportPromptEntry(DateTimeOffset.UtcNow, "Check services", [], "Looks healthy.")
                ],
                GetReportSessionSummary = () => null,
                ShowSuccess = successes.Add
            });

            var result = dispatcher.Dispatch($"/transcript save \"{path}\"");

            result.Handled.Should().BeTrue();
            File.Exists(path).Should().BeTrue();
            successes.Should().Contain(message => message.Contains("Saved redacted transcript", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ShowTranscriptSaveResult_WithFileAlreadyExists_ShouldWarnUser()
    {
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            ShowWarning = warnings.Add
        });
        var method = typeof(SlashCommandDispatcher)
            .GetMethod("ShowTranscriptSaveResult", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.Invoke(dispatcher, [SessionTranscriptSaveResult.FileAlreadyExists, "session.json", null]);

        warnings.Should().Contain(message => message.Contains("already exists", StringComparison.Ordinal));
    }

    [Fact]
    public void Dispatch_WithTranscriptLoadAndExistingHistoryDenied_ShouldNotReplaceHistory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"troublescout-dispatcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "session.json");
        try
        {
            SessionTranscriptService.Save(
                path,
                [new ReportPromptEntry(DateTimeOffset.UtcNow, "Imported prompt", [], "Imported reply")],
                summary: null,
                allowOverwrite: false,
                out _).Should().Be(SessionTranscriptSaveResult.Success);

            var replaceCalls = 0;
            var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
            {
                HasRecordedHistory = () => true,
                ConfirmTranscriptLoadReplace = () => false,
                ReplaceRecordedPrompts = _ => replaceCalls++
            });

            var result = dispatcher.Dispatch($"/transcript load \"{path}\"");

            result.Handled.Should().BeTrue();
            replaceCalls.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Dispatch_WithTranscriptLoad_ShouldReplaceHistoryWithImportedPrompts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"troublescout-dispatcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "session.json");
        try
        {
            SessionTranscriptService.Save(
                path,
                [new ReportPromptEntry(DateTimeOffset.UtcNow, "Imported prompt", [], "Imported reply")],
                summary: null,
                allowOverwrite: false,
                out _).Should().Be(SessionTranscriptSaveResult.Success);

            IReadOnlyList<ReportPromptEntry>? imported = null;
            var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
            {
                HasRecordedHistory = () => false,
                ReplaceRecordedPrompts = prompts => imported = prompts
            });

            var result = dispatcher.Dispatch($"/transcript load \"{path}\"");

            result.Handled.Should().BeTrue();
            imported.Should().NotBeNull();
            imported!.Should().ContainSingle();
            imported![0].Prompt.Should().Be("Imported prompt");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DispatchAsync_WithReasoningAndNoSelectedModel_ShouldWarnUser()
    {
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetSelectedModelInfo = () => null,
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/reasoning high");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("No active model is selected yet. Use /model first.");
    }

    [Fact]
    public async Task DispatchAsync_WithReasoningOnUnsupportedModel_ShouldInformUser()
    {
        var messages = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetSelectedModelInfo = () => new ModelInfo { Id = "gpt-4.1", Name = "GPT 4.1" },
            GetSelectedModelName = () => "gpt-4.1",
            ShowInfo = messages.Add
        });

        var result = await dispatcher.DispatchAsync("/reasoning high");

        result.Handled.Should().BeTrue();
        messages.Should().Contain("The current model 'gpt-4.1' does not expose reasoning-effort controls.");
    }

    [Fact]
    public async Task DispatchAsync_WithReasoningUnsupportedEffort_ShouldWarnAndNotSave()
    {
        var warnings = new List<string>();
        var saveCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetSelectedModelInfo = CreateReasoningModel,
            SaveReasoningEffortState = _ => saveCalls++,
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/reasoning extreme");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("Unsupported reasoning effort 'extreme'. Supported values: low, medium, high or auto.");
        saveCalls.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_WithSameReasoningEffort_ShouldNotSaveOrRestart()
    {
        var messages = new List<string>();
        var saveCalls = 0;
        var restartCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetSelectedModelInfo = CreateReasoningModel,
            GetConfiguredReasoningEffort = () => "high",
            GetReasoningDisplay = _ => "high",
            SaveReasoningEffortState = _ => saveCalls++,
            RecreateCopilotSession = (_, _) =>
            {
                restartCalls++;
                return Task.FromResult(true);
            },
            ShowInfo = messages.Add
        });

        var result = await dispatcher.DispatchAsync("/reasoning high");

        result.Handled.Should().BeTrue();
        messages.Should().Contain("Reasoning remains: high");
        saveCalls.Should().Be(0);
        restartCalls.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_WithReasoningAndNoActiveSession_ShouldSavePreference()
    {
        string? applied = null;
        string? saved = null;
        var successes = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetSelectedModelInfo = CreateReasoningModel,
            ApplyReasoningEffortSetting = value => applied = value,
            SaveReasoningEffortState = value => saved = value,
            GetReasoningDisplay = _ => "medium",
            HasActiveCopilotSession = () => false,
            ShowSuccess = successes.Add
        });

        var result = await dispatcher.DispatchAsync("/reasoning medium");

        result.Handled.Should().BeTrue();
        applied.Should().Be("medium");
        saved.Should().Be("medium");
        successes.Should().Contain("Reasoning preference saved: medium");
    }

    [Fact]
    public async Task DispatchAsync_WithReasoningAndActiveSession_ShouldRestartSessionWithSpinner()
    {
        string? spinnerLabel = null;
        string? restartedModel = null;
        var summaries = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetSelectedModelInfo = CreateReasoningModel,
            GetSelectedModelId = () => "gpt-5",
            ApplyReasoningEffortSetting = _ => { },
            SaveReasoningEffortState = _ => { },
            HasActiveCopilotSession = () => true,
            RunWithSpinnerAsync = async (label, action) =>
            {
                spinnerLabel = label;
                return await action(_ => { });
            },
            RecreateCopilotSession = (model, _) =>
            {
                restartedModel = model;
                return Task.FromResult(true);
            },
            ShowModelSelectionSummary = () => summaries++
        });

        var result = await dispatcher.DispatchAsync("/reasoning high");

        result.Handled.Should().BeTrue();
        spinnerLabel.Should().Be("Applying reasoning high...");
        restartedModel.Should().Be("gpt-5");
        summaries.Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_WithReasoningRestartFailure_ShouldRestorePreviousPreference()
    {
        var applied = new List<string?>();
        var saved = new List<string?>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetSelectedModelInfo = CreateReasoningModel,
            GetSelectedModelId = () => "gpt-5",
            GetConfiguredReasoningEffort = () => "low",
            ApplyReasoningEffortSetting = applied.Add,
            SaveReasoningEffortState = saved.Add,
            HasActiveCopilotSession = () => true,
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            RecreateCopilotSession = (_, _) => Task.FromResult(false)
        });

        var result = await dispatcher.DispatchAsync("/reasoning high");

        result.Handled.Should().BeTrue();
        applied.Should().Equal("high", "low");
        saved.Should().Equal("high", "low");
    }

    private static ModelInfo CreateReasoningModel() =>
        new()
        {
            Id = "gpt-5",
            Name = "GPT 5",
            SupportedReasoningEfforts = ["low", "medium", "high"],
            DefaultReasoningEffort = "medium",
            Capabilities = new ModelCapabilities
            {
                Supports = new ModelSupports
                {
                    ReasoningEffort = true
                }
            }
        };
}
