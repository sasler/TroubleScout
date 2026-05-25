using FluentAssertions;
using GitHub.Copilot;
using TroubleScout.Services;
using TroubleScout.UI;
using Xunit;

namespace TroubleScout.Tests.Services;

[Collection("AppSettings")]
public class SessionPermissionHandlerTests
{
    [Fact]
    public async Task HandleAsync_UrlApproveThisUrl_ShouldPromptOnceForSameUrlAndPromptForDifferentUrl()
    {
        var promptCount = 0;
        var handler = CreateHandler(promptUrlApproval: (_, _) =>
        {
            promptCount++;
            return promptCount == 1
                ? UrlApprovalResult.ApproveThisUrl
                : UrlApprovalResult.Deny;
        });

        var first = await handler.HandleAsync(CreateUrlPermissionRequest("https://example.com/a"), new PermissionInvocation());
        var second = await handler.HandleAsync(CreateUrlPermissionRequest("https://example.com/a"), new PermissionInvocation());
        var third = await handler.HandleAsync(CreateUrlPermissionRequest("https://example.com/b"), new PermissionInvocation());

        first.Kind.Should().Be(PermissionRequestResultKind.Approved);
        second.Kind.Should().Be(PermissionRequestResultKind.Approved);
        third.Kind.Should().Be(PermissionRequestResultKind.Rejected);
        promptCount.Should().Be(2);
    }

    [Fact]
    public async Task HandleAsync_UrlApproveAllUrls_ShouldAutoApproveUntilReset()
    {
        var promptCount = 0;
        var handler = CreateHandler(promptUrlApproval: (_, _) =>
        {
            promptCount++;
            return UrlApprovalResult.ApproveAllUrls;
        });

        var first = await handler.HandleAsync(CreateUrlPermissionRequest("https://example.com/a"), new PermissionInvocation());
        var second = await handler.HandleAsync(CreateUrlPermissionRequest("https://example.com/b"), new PermissionInvocation());

        handler.ResetUrlApprovals();

        var third = await handler.HandleAsync(CreateUrlPermissionRequest("https://example.com/c"), new PermissionInvocation());

        first.Kind.Should().Be(PermissionRequestResultKind.Approved);
        second.Kind.Should().Be(PermissionRequestResultKind.Approved);
        third.Kind.Should().Be(PermissionRequestResultKind.Approved);
        promptCount.Should().Be(2);
    }

    [Fact]
    public async Task HandleAsync_AutoMode_ShouldNotBypassNonShellPermissions()
    {
        var promptCount = 0;
        var handler = CreateHandler(
            getExecutionMode: () => ExecutionMode.Auto,
            promptCommandApproval: (_, _, _) =>
            {
                promptCount++;
                return ApprovalResult.Denied;
            });

        var result = await handler.HandleAsync(new PermissionRequest { Kind = "write" }, new PermissionInvocation());

        result.Kind.Should().Be(PermissionRequestResultKind.Rejected);
        promptCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_CustomToolV1Kind_ShouldAutoApproveWithoutOuterPrompt()
    {
        var promptCount = 0;
        var handler = CreateHandler(promptCommandApproval: (_, _, _) =>
        {
            promptCount++;
            return ApprovalResult.Denied;
        });

        var result = await handler.HandleAsync(
            new PermissionRequestCustomTool
            {
                Kind = "custom_tool",
                ToolName = "run_powershell",
                ToolDescription = "Run a PowerShell diagnostic command"
            },
            new PermissionInvocation());

        result.Kind.Should().Be(PermissionRequestResultKind.Approved);
        promptCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_ShellRequests_ShouldUsePermissionEvaluator()
    {
        var handler = CreateHandler();

        var readOnly = await handler.HandleAsync(
            CreateShellPermissionRequest("Get-ChildItem -Path 'C:\\src\\temp' | Select-Object Name"),
            new PermissionInvocation());
        var blocked = await handler.HandleAsync(
            CreateShellPermissionRequest("Get-Credential"),
            new PermissionInvocation());

        readOnly.Kind.Should().Be(PermissionRequestResultKind.Approved);
        blocked.Kind.Should().Be(PermissionRequestResultKind.Rejected);
    }

    [Fact]
    public async Task HandleAsync_AutoUnknownReadOnlyVerdict_ShouldAuthorizeWithoutHumanPrompt()
    {
        var promptCount = 0;
        var authorizationCount = 0;
        var handler = CreateHandler(
            getExecutionMode: () => ExecutionMode.Auto,
            promptCommandApproval: (_, _, _) =>
            {
                promptCount++;
                return ApprovalResult.Denied;
            },
            autoEvaluator: new FakeAutoEvaluator(new AutoCommandApprovalDecision(true, "gpt-5-mini", "queries inventory")),
            recordAutoAuthorization: (_, _) => authorizationCount++);

        var result = await handler.HandleAsync(
            CreateShellPermissionRequest("Read-CustomInventory -Server localhost"),
            new PermissionInvocation());

        result.Kind.Should().Be(PermissionRequestResultKind.Approved);
        promptCount.Should().Be(0);
        authorizationCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_AutoKnownMutation_ShouldNotInvokeEvaluator()
    {
        var evaluator = new FakeAutoEvaluator(new AutoCommandApprovalDecision(true, "gpt-5-mini", "incorrect"));
        var handler = CreateHandler(
            getExecutionMode: () => ExecutionMode.Auto,
            autoEvaluator: evaluator);

        var result = await handler.HandleAsync(
            CreateShellPermissionRequest("Restart-Service spooler"),
            new PermissionInvocation());

        result.Kind.Should().Be(PermissionRequestResultKind.Rejected);
        evaluator.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_McpApproveServerForSession_ShouldSuppressLaterPromptsAndEmitNoRules()
    {
        var promptCount = 0;
        var handler = CreateHandler(promptMcpApproval: (_, _, _, _) =>
        {
            promptCount++;
            return promptCount == 1
                ? McpApprovalResult.ApproveServerForSession
                : McpApprovalResult.Deny;
        });

        var first = await handler.HandleAsync(
            CreateMcpPermissionRequest("context7", "mutate-docs", "{}"),
            new PermissionInvocation());
        var second = await handler.HandleAsync(
            CreateMcpPermissionRequest("context7", "resolve-id", "{}"),
            new PermissionInvocation());

        first.Kind.Should().Be(PermissionRequestResultKind.Approved);
        (first.Rules?.Count ?? 0).Should().Be(0);
        second.Kind.Should().Be(PermissionRequestResultKind.Approved);
        promptCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_McpApproveOnce_ShouldNotPersistServerApproval()
    {
        var promptCount = 0;
        var handler = CreateHandler(promptMcpApproval: (_, _, _, _) =>
        {
            promptCount++;
            return McpApprovalResult.ApproveOnce;
        });

        var first = await handler.HandleAsync(CreateMcpPermissionRequest("context7", "mutate-docs", "{}"), new PermissionInvocation());
        var second = await handler.HandleAsync(CreateMcpPermissionRequest("context7", "update-docs", "{}"), new PermissionInvocation());

        first.Kind.Should().Be(PermissionRequestResultKind.Approved);
        second.Kind.Should().Be(PermissionRequestResultKind.Approved);
        promptCount.Should().Be(2);
        handler.GetApprovedMcpServersSnapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_McpReadOnlyHeuristic_ShouldAutoApproveReadOnlyButPromptSensitiveReadLikeTools()
    {
        var promptCount = 0;
        var handler = CreateHandler(promptMcpApproval: (_, _, _, _) =>
        {
            promptCount++;
            return McpApprovalResult.Deny;
        });

        var readOnly = await handler.HandleAsync(
            CreateMcpPermissionRequest("Redmine", "Redmine-list_issues", "{}"),
            new PermissionInvocation());
        var sensitive = await handler.HandleAsync(
            CreateMcpPermissionRequest("secrets-server", "vault-read_secret", "{}"),
            new PermissionInvocation());

        readOnly.Kind.Should().Be(PermissionRequestResultKind.Approved);
        sensitive.Kind.Should().Be(PermissionRequestResultKind.Rejected);
        promptCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_McpReadOnlyMetadata_ShouldAutoApproveNonHeuristicToolName()
    {
        var promptCount = 0;
        var handler = CreateHandler(promptMcpApproval: (_, _, _, _) =>
        {
            promptCount++;
            return McpApprovalResult.Deny;
        });

        var readOnly = await handler.HandleAsync(
            CreateMcpPermissionRequest("inventory", "fetchAssetInventory", "{}", readOnly: true),
            new PermissionInvocation());

        readOnly.Kind.Should().Be(PermissionRequestResultKind.Approved);
        promptCount.Should().Be(0);
    }

    [Fact]
    public void SeedPersistedMcpApprovals_ShouldOnlySeedMappedRolesAndClearSeededApprovals()
    {
        WithTemporarySettingsPath(_ =>
        {
            var settings = new AppSettings
            {
                MonitoringMcpServer = "zabbix",
                PersistedApprovedMcpServers = ["zabbix", "orphaned"]
            };
            var handler = CreateHandler(settings: settings, getMcpServerRole: serverName =>
                string.Equals(serverName, "zabbix", StringComparison.OrdinalIgnoreCase) ? "monitoring" : null);

            handler.SeedPersistedMcpApprovals(settings.PersistedApprovedMcpServers);

            handler.GetApprovedMcpServersSnapshot().Should().ContainSingle().Which.Should().Be("zabbix");
            handler.GetPersistedApprovedMcpServersSnapshot().Should().BeEquivalentTo(["zabbix", "orphaned"]);

            var cleared = handler.ClearPersistedMcpApprovals();

            cleared.Should().Be(2);
            handler.GetApprovedMcpServersSnapshot().Should().BeEmpty();
            handler.GetPersistedApprovedMcpServersSnapshot().Should().BeEmpty();
        });
    }

    private static SessionPermissionHandler CreateHandler(
        Func<ExecutionMode>? getExecutionMode = null,
        Func<IReadOnlyList<string>?>? getConfiguredSafeCommands = null,
        Func<string, string?>? getMcpServerRole = null,
        SessionPermissionHandler.CommandApprovalPrompt? promptCommandApproval = null,
        SessionPermissionHandler.UrlApprovalPrompt? promptUrlApproval = null,
        SessionPermissionHandler.McpApprovalPrompt? promptMcpApproval = null,
        AppSettings? settings = null,
        IAutoCommandApprovalEvaluator? autoEvaluator = null,
        Action<string, AutoCommandApprovalDecision>? recordAutoAuthorization = null)
    {
        return new SessionPermissionHandler(
            getExecutionMode ?? (() => ExecutionMode.Strict),
            getConfiguredSafeCommands ?? (() => null),
            getMcpServerRole ?? (_ => null),
            promptCommandApproval ?? ((_, _, _) => ApprovalResult.Denied),
            promptUrlApproval ?? ((_, _) => UrlApprovalResult.Deny),
            promptMcpApproval ?? ((_, _, _, _) => McpApprovalResult.Deny),
            settings,
            autoEvaluator,
            recordAutoAuthorization);
    }

    private static PermissionRequestShell CreateShellPermissionRequest(string command)
    {
        return new PermissionRequestShell
        {
            Kind = "shell",
            FullCommandText = command,
            Intention = "Inspect system state",
            Commands = [],
            PossiblePaths = [],
            PossibleUrls = [],
            HasWriteFileRedirection = false,
            CanOfferSessionApproval = false
        };
    }

    private static PermissionRequestMcp CreateMcpPermissionRequest(string serverName, string? toolName, object? args, bool readOnly = false)
    {
        return new PermissionRequestMcp
        {
            Kind = "mcp",
            ServerName = serverName,
            ToolName = toolName ?? string.Empty,
            ToolTitle = toolName ?? string.Empty,
            Args = args,
            ReadOnly = readOnly
        };
    }

    private static PermissionRequestUrl CreateUrlPermissionRequest(string url, string? intention = null)
    {
        return new PermissionRequestUrl
        {
            Kind = "url-fetch",
            Url = url,
            Intention = intention ?? "Research the issue"
        };
    }

    private static void WithTemporarySettingsPath(Action<string> testAction)
    {
        var originalSettingsPath = AppSettingsStore.SettingsPath;
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"troublescout-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var tempSettingsPath = Path.Combine(tempDirectory, "settings.json");
            AppSettingsStore.SettingsPath = tempSettingsPath;
            testAction(tempSettingsPath);
        }
        finally
        {
            AppSettingsStore.SettingsPath = originalSettingsPath;
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp test files.
            }
        }
    }

    private sealed class FakeAutoEvaluator(AutoCommandApprovalDecision? decision) : IAutoCommandApprovalEvaluator
    {
        internal int CallCount { get; private set; }

        public Task<AutoCommandApprovalDecision?> EvaluateAsync(string command, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(decision);
        }
    }
}
