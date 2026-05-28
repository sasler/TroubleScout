using FluentAssertions;
using GitHub.Copilot;
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
            GetExecutionMode = () => ExecutionMode.Strict,
            ShowInfo = messages.Add
        });

        var result = dispatcher.Dispatch("/mode");

        result.Handled.Should().BeTrue();
        messages.Should().Contain("Current mode: strict");
        messages.Should().Contain("Usage: /mode <strict|auto>");
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
        warnings.Should().Contain("Invalid mode. Use: strict or auto.");
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
            CanEnableAutoMode = () => true,
            ShowStatus = _ => statusCalls++
        });

        var result = dispatcher.Dispatch("/mode auto");

        result.Handled.Should().BeTrue();
        mode.Should().Be(ExecutionMode.Auto);
        statusCalls.Should().Be(1);
    }

    [Fact]
    public void Dispatch_WithAutoModeWithoutApprovalModel_ShouldRefuseChange()
    {
        var warnings = new List<string>();
        var setCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            CanEnableAutoMode = () => false,
            SetExecutionMode = _ => setCalls++,
            ShowWarning = warnings.Add
        });

        dispatcher.Dispatch("/mode auto");

        setCalls.Should().Be(0);
        warnings.Should().Contain(message => message.Contains("/agent-model <model>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchAsync_WithAgentModel_ShouldPersistUnifiedSubagentModelAndRecreateSession()
    {
        (string Role, string? Model)? saved = null;
        var recreateCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            UseByokOpenAi = () => false,
            RefreshAvailableModels = () => Task.CompletedTask,
            GetModelSelectionEntries = () => [CreateModelEntry("gpt-5-mini", "GPT-5 mini", isCurrent: false)],
            SaveAgentModelOverride = (role, model) => saved = (role, model),
            RecreateCurrentCopilotSession = () =>
            {
                recreateCalls++;
                return Task.FromResult((true, (string?)null));
            }
        });

        var result = await dispatcher.DispatchAsync("/agent-model gpt-5-mini");

        result.Handled.Should().BeTrue();
        saved.Should().Be(("subagent", "gpt-5-mini"));
        recreateCalls.Should().Be(1);
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
    public async Task DispatchAsync_WithServerWithoutArgument_ShouldShowUsageAndNotReconnect()
    {
        var warnings = new List<string>();
        var reconnectCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            ReconnectServer = (_, _) =>
            {
                reconnectCalls++;
                return Task.FromResult(true);
            },
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/server");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("Usage: /server <server1>[,server2,...]");
        reconnectCalls.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_WithServerTargetsInSafeMode_ShouldReconnectPromptConnectRefreshRecreateAndShowStatus()
    {
        var spinnerLabels = new List<string>();
        var primaryTargets = new List<string>();
        var approvalPrompts = new List<(string Command, string Reason)>();
        var additionalTargets = new List<(string Server, bool SkipApproval)>();
        var successes = new List<string>();
        var refreshCalls = 0;
        var recreateCalls = 0;
        var statusCalls = new List<bool>();

        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetExecutionMode = () => ExecutionMode.Strict,
            RunWithSpinnerAsync = async (label, action) =>
            {
                spinnerLabels.Add(label);
                return await action(_ => { });
            },
            ReconnectServer = (server, _) =>
            {
                primaryTargets.Add(server);
                return Task.FromResult(true);
            },
            PromptCommandApproval = (command, reason) =>
            {
                approvalPrompts.Add((command, reason));
                return true;
            },
            ConnectAdditionalServer = (server, skipApproval) =>
            {
                additionalTargets.Add((server, skipApproval));
                return Task.FromResult((true, (string?)null));
            },
            RefreshServerContext = () => refreshCalls++,
            RecreateCurrentCopilotSession = () =>
            {
                recreateCalls++;
                return Task.FromResult((true, (string?)null));
            },
            ShowStatus = statusCalls.Add,
            ShowSuccess = successes.Add
        });

        var result = await dispatcher.DispatchAsync("/server srv1,srv2 srv3");

        result.Handled.Should().BeTrue();
        primaryTargets.Should().Equal("srv1");
        successes.Should().Contain("Connected to srv1");
        spinnerLabels.Should().Equal("Connecting to srv1...", "Connecting to srv2...", "Connecting to srv3...");
        approvalPrompts.Should().Equal(
            ("New-PSSession -ComputerName 'srv2'", "TroubleScout wants to establish a direct PowerShell session to srv2"),
            ("New-PSSession -ComputerName 'srv3'", "TroubleScout wants to establish a direct PowerShell session to srv3"));
        additionalTargets.Should().Equal(("srv2", true), ("srv3", true));
        refreshCalls.Should().Be(1);
        recreateCalls.Should().Be(1);
        statusCalls.Should().Equal(false);
    }

    [Fact]
    public async Task DispatchAsync_WithServerAdditionalTargetsAndSessionRecreateFailure_ShouldWarnWithError()
    {
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetExecutionMode = () => ExecutionMode.Auto,
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            ReconnectServer = (_, _) => Task.FromResult(true),
            ConnectAdditionalServer = (_, _) => Task.FromResult((true, (string?)null)),
            RecreateCurrentCopilotSession = () => Task.FromResult((false, (string?)"model unavailable")),
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/server srv1 srv2");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("Connected servers, but the AI session could not be recreated. Use /login or /model to reconnect. model unavailable");
    }

    [Fact]
    public async Task DispatchAsync_WithServerPrimaryOnlySuccess_ShouldRefreshAndShowStatusWithoutSessionRecreate()
    {
        var refreshCalls = 0;
        var recreateCalls = 0;
        var statusCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            ReconnectServer = (_, _) => Task.FromResult(true),
            RefreshServerContext = () => refreshCalls++,
            RecreateCurrentCopilotSession = () =>
            {
                recreateCalls++;
                return Task.FromResult((true, (string?)null));
            },
            ShowStatus = _ => statusCalls++
        });

        var result = await dispatcher.DispatchAsync("/server srv1");

        result.Handled.Should().BeTrue();
        refreshCalls.Should().Be(1);
        recreateCalls.Should().Be(0);
        statusCalls.Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_WithServerAdditionalDeniedInSafeMode_ShouldWarnAndSkipConnection()
    {
        var warnings = new List<string>();
        var additionalCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetExecutionMode = () => ExecutionMode.Strict,
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            ReconnectServer = (_, _) => Task.FromResult(true),
            PromptCommandApproval = (_, _) => false,
            ConnectAdditionalServer = (_, _) =>
            {
                additionalCalls++;
                return Task.FromResult((true, (string?)null));
            },
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/server srv1 srv2");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("Connection to srv2 was denied.");
        additionalCalls.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_WithServerAdditionalInAutoMode_ShouldStillPromptForConnection()
    {
        var approvalCalls = 0;
        var additionalTargets = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetExecutionMode = () => ExecutionMode.Auto,
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            ReconnectServer = (_, _) => Task.FromResult(true),
            PromptCommandApproval = (_, _) =>
            {
                approvalCalls++;
                return true;
            },
            ConnectAdditionalServer = (server, _) =>
            {
                additionalTargets.Add(server);
                return Task.FromResult((true, (string?)null));
            }
        });

        var result = await dispatcher.DispatchAsync("/server srv1 srv2");

        result.Handled.Should().BeTrue();
        approvalCalls.Should().Be(1);
        additionalTargets.Should().Equal("srv2");
    }

    [Fact]
    public async Task DispatchAsync_WithServerPrimaryReconnectFailure_ShouldStopAdditionalWork()
    {
        var additionalCalls = 0;
        var refreshCalls = 0;
        var recreateCalls = 0;
        var statusCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            ReconnectServer = (_, _) => Task.FromResult(false),
            ConnectAdditionalServer = (_, _) =>
            {
                additionalCalls++;
                return Task.FromResult((true, (string?)null));
            },
            RefreshServerContext = () => refreshCalls++,
            RecreateCurrentCopilotSession = () =>
            {
                recreateCalls++;
                return Task.FromResult((true, (string?)null));
            },
            ShowStatus = _ => statusCalls++
        });

        var result = await dispatcher.DispatchAsync("/server srv1 srv2");

        result.Handled.Should().BeTrue();
        additionalCalls.Should().Be(0);
        refreshCalls.Should().Be(0);
        recreateCalls.Should().Be(0);
        statusCalls.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_WithJeaWithoutArguments_ShouldPromptAndConnectWithSkipApproval()
    {
        var prompts = new Queue<string>(["srv1", "JEA-Admins"]);
        var infos = new List<string>();
        var connectCalls = new List<(string Server, string Configuration, bool SkipApproval)>();

        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            PromptText = () => prompts.Dequeue(),
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            ConnectJeaServer = (server, configuration, skipApproval) =>
            {
                connectCalls.Add((server, configuration, skipApproval));
                return Task.FromResult((true, (string?)null));
            },
            ShowInfo = infos.Add
        });

        var result = await dispatcher.DispatchAsync("/jea");

        result.Handled.Should().BeTrue();
        infos.Should().Contain("Enter the server name for the JEA session:");
        infos.Should().Contain("Enter the JEA configuration name:");
        infos.Should().Contain("Example: /jea server1 JEA-Admins");
        connectCalls.Should().Equal(("srv1", "JEA-Admins", true));
    }

    [Fact]
    public async Task DispatchAsync_WithJeaEmptyGuidedServer_ShouldWarnAndNotConnect()
    {
        var warnings = new List<string>();
        var connectCalls = 0;

        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            PromptText = () => " ",
            ConnectJeaServer = (_, _, _) =>
            {
                connectCalls++;
                return Task.FromResult((true, (string?)null));
            },
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/jea");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("Server name cannot be empty.");
        connectCalls.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_WithJeaEmptyGuidedConfiguration_ShouldWarnAndNotConnect()
    {
        var prompts = new Queue<string>(["srv1", " "]);
        var warnings = new List<string>();
        var connectCalls = 0;

        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            PromptText = () => prompts.Dequeue(),
            ConnectJeaServer = (_, _, _) =>
            {
                connectCalls++;
                return Task.FromResult((true, (string?)null));
            },
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/jea");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("Configuration name cannot be empty.");
        connectCalls.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_WithJeaConfigurationContainingSpaces_ShouldJoinConfigurationName()
    {
        var connectCalls = new List<(string Server, string Configuration, bool SkipApproval)>();

        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            ConnectJeaServer = (server, configuration, skipApproval) =>
            {
                connectCalls.Add((server, configuration, skipApproval));
                return Task.FromResult((true, (string?)null));
            }
        });

        var result = await dispatcher.DispatchAsync("/jea srv1 JEA Admin Role");

        result.Handled.Should().BeTrue();
        connectCalls.Should().Equal(("srv1", "JEA Admin Role", true));
    }

    [Fact]
    public async Task DispatchAsync_WithJeaSuccess_ShouldShowCommandsRefreshRecreateAndShowStatus()
    {
        var spinnerLabels = new List<string>();
        var successes = new List<string>();
        var renderedCommands = new List<(string Server, string Configuration, IReadOnlyCollection<string> Commands)>();
        var refreshCalls = 0;
        var recreateCalls = 0;
        var statusCalls = new List<bool>();

        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            RunWithSpinnerAsync = async (label, action) =>
            {
                spinnerLabels.Add(label);
                return await action(_ => { });
            },
            ConnectJeaServer = (_, _, _) => Task.FromResult((true, (string?)null)),
            GetJeaAllowedCommands = _ => new[] { "Get-Service", "Restart-Service" },
            ShowJeaDiscoveredCommands = (server, configuration, commands) =>
                renderedCommands.Add((server, configuration, commands.ToArray())),
            RefreshServerContext = () => refreshCalls++,
            RecreateCurrentCopilotSession = () =>
            {
                recreateCalls++;
                return Task.FromResult((true, (string?)null));
            },
            ShowStatus = statusCalls.Add,
            ShowSuccess = successes.Add
        });

        var result = await dispatcher.DispatchAsync("/jea srv1 JEA-Admins");

        result.Handled.Should().BeTrue();
        spinnerLabels.Should().Equal("Connecting to JEA endpoint JEA-Admins on srv1...");
        successes.Should().Contain("Connected to JEA endpoint 'JEA-Admins' on srv1");
        renderedCommands.Should().ContainSingle();
        renderedCommands[0].Server.Should().Be("srv1");
        renderedCommands[0].Configuration.Should().Be("JEA-Admins");
        renderedCommands[0].Commands.Should().BeEquivalentTo("Get-Service", "Restart-Service");
        refreshCalls.Should().Be(1);
        recreateCalls.Should().Be(1);
        statusCalls.Should().Equal(false);
    }

    [Fact]
    public async Task DispatchAsync_WithJeaConnectFailure_ShouldWarnAndSkipRefreshRecreateStatus()
    {
        var warnings = new List<string>();
        var refreshCalls = 0;
        var recreateCalls = 0;
        var statusCalls = 0;

        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            ConnectJeaServer = (_, _, _) => Task.FromResult((false, (string?)"access denied")),
            RefreshServerContext = () => refreshCalls++,
            RecreateCurrentCopilotSession = () =>
            {
                recreateCalls++;
                return Task.FromResult((true, (string?)null));
            },
            ShowStatus = _ => statusCalls++,
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/jea srv1 JEA-Admins");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("access denied");
        refreshCalls.Should().Be(0);
        recreateCalls.Should().Be(0);
        statusCalls.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_WithJeaSessionRecreateFailure_ShouldWarnWithError()
    {
        var warnings = new List<string>();

        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            ConnectJeaServer = (_, _, _) => Task.FromResult((true, (string?)null)),
            RecreateCurrentCopilotSession = () => Task.FromResult((false, (string?)"model unavailable")),
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/jea srv1 JEA-Admins");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("Connected JEA endpoint, but the AI session could not be recreated. Use /login or /model to reconnect. model unavailable");
    }

    [Fact]
    public async Task DispatchAsync_WithJeaLikeUnknownCommand_ShouldFallThrough()
    {
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers());

        var result = await dispatcher.DispatchAsync("/jeaX srv01 JEA-Admins");

        result.Handled.Should().BeFalse();
        result.ExitRequested.Should().BeFalse();
    }

    [Fact]
    public async Task DispatchAsync_WithServerLikeUnknownCommand_ShouldFallThrough()
    {
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers());

        var result = await dispatcher.DispatchAsync("/serverX srv01");

        result.Handled.Should().BeFalse();
        result.ExitRequested.Should().BeFalse();
    }

    [Fact]
    public async Task DispatchAsync_WithClearSuccess_ShouldResetAndRenderNewSession()
    {
        var calls = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            ResetConversation = () =>
            {
                calls.Add("reset");
                return Task.FromResult(true);
            },
            ClearConsole = () => calls.Add("clear-console"),
            ShowBanner = () => calls.Add("banner"),
            ShowStatus = includeApprovals => calls.Add($"status:{includeApprovals}"),
            GetSessionId = () => "session-123",
            GetWelcomeHint = () => "welcome hint",
            ShowWelcomeMessage = hint => calls.Add($"welcome:{hint}"),
            ShowSuccess = message => calls.Add($"success:{message}")
        });

        var result = await dispatcher.DispatchAsync("/clear");

        result.Handled.Should().BeTrue();
        calls.Should().Equal(
            "reset",
            "clear-console",
            "banner",
            "status:False",
            "success:Started new session: session-123",
            "welcome:welcome hint");
    }

    [Fact]
    public async Task DispatchAsync_WithClearFailure_ShouldWarnUser()
    {
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            ResetConversation = () => Task.FromResult(false),
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/clear");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("Could not start a new session.");
    }

    [Fact]
    public async Task DispatchAsync_WithSettingsSuccess_ShouldOpenReloadApplyThemeInvalidateAndRecreate()
    {
        var calls = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            EnsureSettingsFile = () => calls.Add("ensure"),
            GetSettingsPath = () => @"C:\Temp\settings.json",
            OpenSettingsEditor = path =>
            {
                calls.Add($"open:{path}");
                return Task.FromResult((string?)null);
            },
            ReloadSettings = () => calls.Add("reload"),
            GetPersistedTheme = () => "mono",
            SetTheme = theme => calls.Add($"theme:{theme}"),
            InvalidateModelCache = () => calls.Add("invalidate-models"),
            RecreateCurrentCopilotSession = () =>
            {
                calls.Add("recreate");
                return Task.FromResult((true, (string?)null));
            },
            ShowInfo = message => calls.Add($"info:{message}"),
            ShowSuccess = message => calls.Add($"success:{message}")
        });

        var result = await dispatcher.DispatchAsync("/settings");

        result.Handled.Should().BeTrue();
        calls.Should().Equal(
            "ensure",
            @"info:Settings file: C:\Temp\settings.json",
            @"open:C:\Temp\settings.json",
            "reload",
            "theme:mono",
            "invalidate-models",
            "recreate",
            "success:Settings reloaded. Safe command patterns and system prompt settings have been applied.");
    }

    [Fact]
    public async Task DispatchAsync_WithSettingsEditorAndRecreateErrors_ShouldWarnBoth()
    {
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetSettingsPath = () => "settings.json",
            OpenSettingsEditor = _ => Task.FromResult((string?)"editor unavailable"),
            RecreateCurrentCopilotSession = () => Task.FromResult((false, (string?)"model unavailable")),
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/settings");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("editor unavailable");
        warnings.Should().Contain("Settings were reloaded, but the AI session could not be recreated. Use /login or /model to reconnect. model unavailable");
    }

    [Fact]
    public async Task DispatchAsync_WithLoginSuccess_ShouldInvalidateRunSpinnerAndShowSuccess()
    {
        var calls = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            InvalidateModelCache = () => calls.Add("invalidate-models"),
            RunWithSpinnerAsync = async (label, action) =>
            {
                calls.Add($"spinner:{label}");
                return await action(status => calls.Add($"status:{status}"));
            },
            LoginAndCreateGitHubSession = updateStatus =>
            {
                updateStatus("logging in");
                calls.Add("login");
                return Task.FromResult(true);
            },
            ShowSuccess = message => calls.Add($"success:{message}")
        });

        var result = await dispatcher.DispatchAsync("/login");

        result.Handled.Should().BeTrue();
        calls.Should().Equal(
            "invalidate-models",
            "spinner:Running Copilot login...",
            "status:logging in",
            "login",
            "success:GitHub Copilot login completed and session is ready.");
    }

    [Fact]
    public async Task DispatchAsync_WithLoginFailure_ShouldNotShowSuccess()
    {
        var successes = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            LoginAndCreateGitHubSession = _ => Task.FromResult(false),
            ShowSuccess = successes.Add
        });

        var result = await dispatcher.DispatchAsync("/login");

        result.Handled.Should().BeTrue();
        successes.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_WithMcpRoleInteractiveSelection_ShouldSaveReloadRecreateAndShowStatus()
    {
        string? savedMonitoring = null;
        string? savedTicketing = null;
        var calls = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetAvailableMcpRoleServerNames = () => ["redmine", "zabbix"],
            PromptMcpRoleSelection = (monitoring, ticketing, servers) =>
            {
                monitoring.Should().BeNull();
                ticketing.Should().BeNull();
                servers.Should().BeEquivalentTo("redmine", "zabbix");
                return ("zabbix", "redmine");
            },
            SaveMcpRoleSettings = (monitoring, ticketing) =>
            {
                savedMonitoring = monitoring;
                savedTicketing = ticketing;
                calls.Add("save");
            },
            ReloadSettings = () => calls.Add("reload"),
            RecreateCurrentCopilotSession = () =>
            {
                calls.Add("recreate");
                return Task.FromResult((true, (string?)null));
            },
            ShowSuccess = calls.Add,
            ShowStatus = includeApprovals => calls.Add($"status:{includeApprovals}")
        });

        var result = await dispatcher.DispatchAsync("/mcp-role");

        result.Handled.Should().BeTrue();
        savedMonitoring.Should().Be("zabbix");
        savedTicketing.Should().Be("redmine");
        calls.Should().Equal(
            "save",
            "reload",
            "recreate",
            "MCP roles saved. Monitoring: zabbix | Ticketing: redmine",
            "status:False");
    }

    [Fact]
    public async Task DispatchAsync_WithMcpRoleDirectMonitoringAssignment_ShouldPersistResolvedServer()
    {
        string? savedMonitoring = null;
        string? savedTicketing = "unchanged";
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetAvailableMcpRoleServerNames = () => ["Zabbix"],
            SaveMcpRoleSettings = (monitoring, ticketing) =>
            {
                savedMonitoring = monitoring;
                savedTicketing = ticketing;
            }
        });

        var result = await dispatcher.DispatchAsync("/mcp-role monitoring zabbix");

        result.Handled.Should().BeTrue();
        savedMonitoring.Should().Be("Zabbix");
        savedTicketing.Should().BeNull();
    }

    [Fact]
    public async Task DispatchAsync_WithMcpRoleClearAll_ShouldClearBothRoles()
    {
        string? savedMonitoring = "unchanged";
        string? savedTicketing = "unchanged";
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetConfiguredMonitoringMcpServer = () => "zabbix",
            GetConfiguredTicketingMcpServer = () => "redmine",
            SaveMcpRoleSettings = (monitoring, ticketing) =>
            {
                savedMonitoring = monitoring;
                savedTicketing = ticketing;
            }
        });

        var result = await dispatcher.DispatchAsync("/mcp-role clear all");

        result.Handled.Should().BeTrue();
        savedMonitoring.Should().BeNull();
        savedTicketing.Should().BeNull();
    }

    [Fact]
    public async Task DispatchAsync_WithMcpRoleUnchanged_ShouldNotSaveAndShouldShowStatus()
    {
        var saveCalls = 0;
        var messages = new List<string>();
        var statusCalls = new List<bool>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetConfiguredMonitoringMcpServer = () => "zabbix",
            GetConfiguredTicketingMcpServer = () => "redmine",
            GetAvailableMcpRoleServerNames = () => ["zabbix", "redmine"],
            SaveMcpRoleSettings = (_, _) => saveCalls++,
            ShowInfo = messages.Add,
            ShowStatus = statusCalls.Add
        });

        var result = await dispatcher.DispatchAsync("/mcp-role monitoring zabbix");

        result.Handled.Should().BeTrue();
        saveCalls.Should().Be(0);
        messages.Should().Contain("MCP roles unchanged. Monitoring: zabbix | Ticketing: redmine");
        statusCalls.Should().Equal(false);
    }

    [Fact]
    public async Task DispatchAsync_WithMcpRoleInvalidUsage_ShouldWarnAndShowUsage()
    {
        var warnings = new List<string>();
        var infos = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            ShowWarning = warnings.Add,
            ShowInfo = infos.Add
        });

        var result = await dispatcher.DispatchAsync("/mcp-role clear");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("Specify which role to clear: monitoring, ticketing, or all.");
        infos.Should().Contain("Usage:");
        infos.Should().Contain("  /mcp-role clear <monitoring|ticketing|all>");
    }

    [Fact]
    public async Task DispatchAsync_WithMcpRoleUnknownServer_ShouldWarnUser()
    {
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetAvailableMcpRoleServerNames = () => ["zabbix"],
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/mcp-role ticketing redmine");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("Unknown MCP server 'redmine'. Use /capabilities to see configured MCP servers.");
    }

    [Fact]
    public async Task DispatchAsync_WithMcpRoleNoConfiguredServers_ShouldWarnUser()
    {
        var warnings = new List<string>();
        var promptCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            PromptMcpRoleSelection = (_, _, _) =>
            {
                promptCalls++;
                return (null, null);
            },
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/mcp-role");

        result.Handled.Should().BeTrue();
        promptCalls.Should().Be(0);
        warnings.Should().Contain("No MCP servers are configured. Add servers in your MCP config first, then use /mcp-role.");
    }

    [Fact]
    public async Task DispatchAsync_WithMcpRoleRecreateFailure_ShouldWarnWithError()
    {
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetAvailableMcpRoleServerNames = () => ["zabbix"],
            RecreateCurrentCopilotSession = () => Task.FromResult((false, (string?)"model unavailable")),
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/mcp-role monitoring zabbix");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("MCP roles were saved, but the AI session could not be recreated. Use /login or /model to reconnect. model unavailable");
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

    [Theory]
    [InlineData("/byok clear")]
    [InlineData("/byok off")]
    [InlineData("/byok disable")]
    public async Task DispatchAsync_WithByokClear_ShouldClearSettingsRuntimeStateAndCache(string input)
    {
        (bool Enabled, string? BaseUrl, string? ApiKey)? saved = null;
        var runtimeCleared = 0;
        var cacheInvalidated = 0;
        var successes = new List<string>();
        var messages = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetOpenAiApiKeyEnvironmentVariable = () => "TROUBLESCOUT_TEST_API_KEY",
            SaveByokSettings = (enabled, baseUrl, apiKey) => saved = (enabled, baseUrl, apiKey),
            ClearByokRuntimeState = () => runtimeCleared++,
            InvalidateModelCache = () => cacheInvalidated++,
            ShowSuccess = successes.Add,
            ShowInfo = messages.Add
        });

        var result = await dispatcher.DispatchAsync(input);

        result.Handled.Should().BeTrue();
        saved.Should().NotBeNull();
        saved!.Value.Enabled.Should().BeFalse();
        saved.Value.BaseUrl.Should().BeNull();
        saved.Value.ApiKey.Should().BeNull();
        runtimeCleared.Should().Be(1);
        cacheInvalidated.Should().Be(1);
        successes.Should().Contain("Saved BYOK settings cleared for this profile.");
        messages.Should().Contain("Current session provider remains unchanged until you switch model/provider or restart.");
        messages.Should().Contain("The TROUBLESCOUT_TEST_API_KEY environment variable (if set) is unchanged.");
    }

    [Fact]
    public async Task DispatchAsync_WithByokInteractivePrompt_ShouldConfigureEnteredBaseUrlAndApiKey()
    {
        var prompts = new Queue<string>(["https://proxy.example/v1", "sk-test"]);
        (string BaseUrl, string ApiKey, string? Model)? configured = null;
        var summaries = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetByokBaseUrl = () => "https://api.openai.com/v1",
            GetDefaultByokModel = () => "gpt-5",
            PromptText = () => prompts.Dequeue(),
            ConfigureByokOpenAi = (baseUrl, apiKey, model, _) =>
            {
                configured = (baseUrl, apiKey, model);
                return Task.FromResult(true);
            },
            ShowModelSelectionSummary = () => summaries++
        });

        var result = await dispatcher.DispatchAsync("/byok");

        result.Handled.Should().BeTrue();
        configured.Should().Be(("https://proxy.example/v1", "sk-test", "gpt-5"));
        summaries.Should().Be(1);
    }

    [Theory]
    [InlineData("/byok env https://api.openai.com/v1 gpt-5", "env-secret", "https://api.openai.com/v1", "gpt-5")]
    [InlineData("/byok sk-test https://aigw.example.org gpt-5", "sk-test", "https://aigw.example.org", "gpt-5")]
    [InlineData("/byok https://aigw.example.org sk-test gpt-5", "sk-test", "https://aigw.example.org", "gpt-5")]
    [InlineData("/byok sk-test gpt-4.1", "sk-test", "https://api.openai.com/v1", "gpt-4.1")]
    public async Task DispatchAsync_WithByokArguments_ShouldParseSupportedForms(
        string input,
        string expectedApiKey,
        string expectedBaseUrl,
        string expectedModel)
    {
        (string BaseUrl, string ApiKey, string? Model)? configured = null;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetOpenAiApiKeyEnvironmentVariable = () => "TROUBLESCOUT_TEST_API_KEY",
            GetByokBaseUrl = () => "https://api.openai.com/v1",
            GetDefaultByokModel = () => "fallback-model",
            GetEnvironmentVariable = name => name == "TROUBLESCOUT_TEST_API_KEY" ? "env-secret" : null,
            ConfigureByokOpenAi = (baseUrl, apiKey, model, _) =>
            {
                configured = (baseUrl, apiKey, model);
                return Task.FromResult(true);
            }
        });

        var result = await dispatcher.DispatchAsync(input);

        result.Handled.Should().BeTrue();
        configured.Should().Be((expectedBaseUrl, expectedApiKey, expectedModel));
    }

    [Fact]
    public async Task DispatchAsync_WithByokMissingApiKey_ShouldWarnAndShowExamples()
    {
        var warnings = new List<string>();
        var messages = new List<string>();
        var configureCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetOpenAiApiKeyEnvironmentVariable = () => "TROUBLESCOUT_TEST_API_KEY",
            GetEnvironmentVariable = _ => null,
            ConfigureByokOpenAi = (_, _, _, _) =>
            {
                configureCalls++;
                return Task.FromResult(true);
            },
            ShowWarning = warnings.Add,
            ShowInfo = messages.Add
        });

        var result = await dispatcher.DispatchAsync("/byok env");

        result.Handled.Should().BeTrue();
        configureCalls.Should().Be(0);
        warnings.Should().Contain("No API key was provided. Set TROUBLESCOUT_TEST_API_KEY or pass it as /byok <api-key> [base-url] [model].");
        messages.Should().Contain("Examples:");
        messages.Should().Contain("  /byok env https://api.openai.com/v1");
        messages.Should().Contain("  /byok sk-... https://aigw.example.org");
    }

    [Fact]
    public async Task DispatchAsync_WithByokInvalidBaseUrl_ShouldWarnAndNotConfigure()
    {
        var warnings = new List<string>();
        var configureCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetByokBaseUrl = () => "not-a-url",
            ConfigureByokOpenAi = (_, _, _, _) =>
            {
                configureCalls++;
                return Task.FromResult(true);
            },
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/byok sk-test");

        result.Handled.Should().BeTrue();
        configureCalls.Should().Be(0);
        warnings.Should().Contain("Base URL is invalid. Example: https://api.openai.com/v1");
    }

    [Fact]
    public async Task DispatchAsync_WithByokConfigureSuccess_ShouldInvalidateCacheAndShowSummary()
    {
        var cacheInvalidated = 0;
        var summaries = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetByokBaseUrl = () => "https://api.openai.com/v1",
            ConfigureByokOpenAi = (_, _, _, _) => Task.FromResult(true),
            InvalidateModelCache = () => cacheInvalidated++,
            ShowModelSelectionSummary = () => summaries++
        });

        var result = await dispatcher.DispatchAsync("/byok sk-test");

        result.Handled.Should().BeTrue();
        cacheInvalidated.Should().Be(1);
        summaries.Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_WithModelAndNoAvailableModelsAfterRefresh_ShouldWarnUser()
    {
        var refreshCalls = 0;
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            HasCopilotClient = () => true,
            RefreshAvailableModels = () =>
            {
                refreshCalls++;
                return Task.CompletedTask;
            },
            GetAvailableModelCount = () => 0,
            ShowWarning = warnings.Add
        });

        var result = await dispatcher.DispatchAsync("/model");

        result.Handled.Should().BeTrue();
        refreshCalls.Should().Be(1);
        warnings.Should().Contain("No models available. Authenticate with /login and/or configure BYOK with /byok.");
        warnings.Should().Contain("No models are available yet. Authenticate GitHub Copilot or configure BYOK, then try /model again.");
    }

    [Fact]
    public async Task DispatchAsync_WithModelSelectionCanceled_ShouldKeepCurrentModel()
    {
        var messages = new List<string>();
        var changeCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetAvailableModelCount = () => 1,
            GetSelectedModelName = () => "gpt-4.1",
            GetModelSelectionEntries = () => [CreateModelEntry("gpt-5", "GPT 5 (GitHub Copilot)", isCurrent: false)],
            PromptModelSelection = (_, _) => null,
            ChangeModel = (_, _) =>
            {
                changeCalls++;
                return Task.FromResult(true);
            },
            ShowInfo = messages.Add
        });

        var result = await dispatcher.DispatchAsync("/model");

        result.Handled.Should().BeTrue();
        messages.Should().Contain("Keeping current model: gpt-4.1");
        changeCalls.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_WithModelCleanSessionSwitch_ShouldChangeModelClearHistoryAndShowSummary()
    {
        var entry = CreateModelEntry("gpt-5", "GPT 5 (GitHub Copilot)", isCurrent: false);
        var spinnerLabels = new List<string>();
        var changedEntries = new List<ModelSelectionEntry>();
        var historyClears = 0;
        var summaries = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetAvailableModelCount = () => 1,
            GetSelectedModelName = () => "gpt-4.1",
            GetModelSelectionEntries = () => [entry],
            PromptModelSelection = (_, _) => entry,
            IsCurrentModelAndSource = _ => false,
            HasRecordedHistory = () => true,
            RunWithSpinnerAsync = async (label, action) =>
            {
                spinnerLabels.Add(label);
                return await action(_ => { });
            },
            ChangeModel = (selectedEntry, _) =>
            {
                changedEntries.Add(selectedEntry);
                return Task.FromResult(true);
            },
            ClearRecordedHistory = () => historyClears++,
            ShowModelSelectionSummary = () => summaries++
        });

        var result = await dispatcher.DispatchAsync("/model");

        result.Handled.Should().BeTrue();
        spinnerLabels.Should().Contain("Switching to GPT 5 (GitHub Copilot) with delegated model GPT 5 (GitHub Copilot)...");
        changedEntries.Should().ContainSingle().Which.Should().Be(entry);
        historyClears.Should().Be(1);
        summaries.Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_WithSubagentOnlyModelSwitch_ShouldRecreateCleanSession()
    {
        var primary = CreateModelEntry("gpt-4.1", "GPT 4.1 (GitHub Copilot)", isCurrent: true);
        var delegated = CreateModelEntry("gpt-5-mini", "GPT 5 mini (GitHub Copilot)", isCurrent: false);
        var historyClears = 0;
        var recreateCalls = 0;
        (string Role, string? Model)? saved = null;
        var runtimeModels = new List<string?>();
        var prompts = new Queue<ModelSelectionEntry>([primary, delegated]);
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetAvailableModelCount = () => 2,
            GetSelectedModelName = () => "gpt-4.1",
            GetModelSelectionEntries = () => [primary, delegated],
            GetAgentModelOverrides = () => new Dictionary<string, string> { ["subagent"] = "gpt-4.1" },
            PromptModelSelection = (_, _) => prompts.Dequeue(),
            IsCurrentModelAndSource = entry => entry.ModelId == "gpt-4.1",
            HasRecordedHistory = () => true,
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            RecreateCurrentCopilotSession = () =>
            {
                recreateCalls++;
                runtimeModels.Should().Contain("gpt-5-mini");
                return Task.FromResult((true, (string?)null));
            },
            ApplyRuntimeAgentModelOverride = (_, model) => runtimeModels.Add(model),
            SaveAgentModelOverride = (role, model) => saved = (role, model),
            ClearRecordedHistory = () => historyClears++,
            ShowModelSelectionSummary = () => { }
        });

        var result = await dispatcher.DispatchAsync("/model");

        result.Handled.Should().BeTrue();
        recreateCalls.Should().Be(1);
        historyClears.Should().Be(1);
        saved.Should().Be(("subagent", "gpt-5-mini"));
        runtimeModels.Should().Contain("gpt-5-mini");
    }

    [Fact]
    public async Task DispatchAsync_WithFailedSubagentOnlySwitch_ShouldRestoreRuntimeOverrideAndNotPersist()
    {
        var primary = CreateModelEntry("gpt-4.1", "GPT 4.1 (GitHub Copilot)", isCurrent: true);
        var delegated = CreateModelEntry("gpt-5-mini", "GPT 5 mini (GitHub Copilot)", isCurrent: false);
        var prompts = new Queue<ModelSelectionEntry>([primary, delegated]);
        var runtimeModels = new List<string?>();
        var saves = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetAvailableModelCount = () => 2,
            GetSelectedModelName = () => "gpt-4.1",
            GetModelSelectionEntries = () => [primary, delegated],
            GetAgentModelOverrides = () => new Dictionary<string, string> { ["subagent"] = "gpt-4.1" },
            PromptModelSelection = (_, _) => prompts.Dequeue(),
            IsCurrentModelAndSource = entry => entry.ModelId == "gpt-4.1",
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            RecreateCurrentCopilotSession = () => Task.FromResult((false, (string?)"failed")),
            ApplyRuntimeAgentModelOverride = (_, model) => runtimeModels.Add(model),
            SaveAgentModelOverride = (_, _) => saves++
        });

        await dispatcher.DispatchAsync("/model");

        saves.Should().Be(0);
        runtimeModels.Should().Equal("gpt-5-mini", "gpt-4.1");
    }

    [Fact]
    public async Task DispatchAsync_WithCrossProviderModelSwitch_ShouldPersistSubagentAfterPrimarySwitch()
    {
        var primary = CreateModelEntry("gpt-byok", "GPT BYOK", isCurrent: false, ModelSource.Byok);
        var delegated = CreateModelEntry("gpt-byok-mini", "GPT BYOK mini", isCurrent: false, ModelSource.Byok);
        var prompts = new Queue<ModelSelectionEntry>([primary, delegated]);
        var switchCompleted = false;
        var savedAfterSwitch = false;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetAvailableModelCount = () => 2,
            GetSelectedModelName = () => "gpt-github",
            GetModelSelectionEntries = () => [primary, delegated],
            PromptModelSelection = (_, _) => prompts.Dequeue(),
            IsCurrentModelAndSource = _ => false,
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            ChangeModel = (_, _) =>
            {
                switchCompleted = true;
                return Task.FromResult(true);
            },
            SaveAgentModelOverride = (_, _) => savedAfterSwitch = switchCompleted,
            ShowModelSelectionSummary = () => { }
        });

        await dispatcher.DispatchAsync("/model");

        savedAfterSwitch.Should().BeTrue();
    }

    [Fact]
    public async Task DispatchAsync_WithFailedPrimaryModelSwitch_ShouldNotPersistSubagentSelection()
    {
        var primary = CreateModelEntry("gpt-byok", "GPT BYOK", isCurrent: false, ModelSource.Byok);
        var prompts = new Queue<ModelSelectionEntry>([primary, primary]);
        var saves = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetAvailableModelCount = () => 1,
            GetSelectedModelName = () => "gpt-github",
            GetModelSelectionEntries = () => [primary],
            PromptModelSelection = (_, _) => prompts.Dequeue(),
            IsCurrentModelAndSource = _ => false,
            RunWithSpinnerAsync = async (_, action) => await action(_ => { }),
            ChangeModel = (_, _) => Task.FromResult(false),
            SaveAgentModelOverride = (_, _) => saves++
        });

        await dispatcher.DispatchAsync("/model");

        saves.Should().Be(0);
    }

    private static ModelSelectionEntry CreateModelEntry(
        string modelId,
        string displayName,
        bool isCurrent,
        ModelSource source = ModelSource.GitHub)
        => new(modelId, displayName, source)
        {
            IsCurrent = isCurrent
        };

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
