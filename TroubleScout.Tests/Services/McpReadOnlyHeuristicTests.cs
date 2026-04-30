using FluentAssertions;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

/// <summary>
/// Pins the read-only MCP heuristic that gates auto-approval. Adding a tool name
/// that should be approved-without-prompt or removing an entry that classifies a
/// sensitive tool as safe both directly affect Safe-mode guarantees, so this is
/// part of the invariant test pass.
/// </summary>
public class McpReadOnlyHeuristicTests
{
    [Theory]
    [InlineData("get_users")]
    [InlineData("list_issues")]
    [InlineData("search_repos")]
    [InlineData("find_alerts")]
    [InlineData("describe_host")]
    [InlineData("read_metric")]
    [InlineData("query_events")]
    [InlineData("inspect_node")]
    [InlineData("show_config")]
    [InlineData("fetch_status")]
    [InlineData("lookup_record")]
    public void IsReadOnlyToolName_DocumentedPrefixes_AreReadOnly(string toolName)
    {
        McpReadOnlyHeuristic.IsReadOnlyToolName(toolName).Should().BeTrue();
    }

    [Theory]
    [InlineData("get")]
    [InlineData("list")]
    [InlineData("search")]
    [InlineData("ping")]
    [InlineData("status")]
    [InlineData("version")]
    [InlineData("capabilities")]
    public void IsReadOnlyToolName_ExactSafeNames_AreReadOnly(string toolName)
    {
        McpReadOnlyHeuristic.IsReadOnlyToolName(toolName).Should().BeTrue();
    }

    [Theory]
    [InlineData("create_issue")]
    [InlineData("delete_user")]
    [InlineData("update_record")]
    [InlineData("post_message")]
    [InlineData("put_object")]
    [InlineData("patch_resource")]
    [InlineData("set_config")]
    [InlineData("write_file")]
    [InlineData("execute_command")]
    [InlineData("run_script")]
    public void IsReadOnlyToolName_MutatingNames_AreNotReadOnly(string toolName)
    {
        McpReadOnlyHeuristic.IsReadOnlyToolName(toolName).Should().BeFalse();
    }

    [Theory]
    [InlineData("get_credential")]
    [InlineData("get_secret")]
    [InlineData("read_password")]
    [InlineData("fetch_token")]
    [InlineData("list_api_keys")]
    [InlineData("get_api_key")]
    [InlineData("query_apikey")]
    [InlineData("read_access_token")]
    [InlineData("get_refresh_token")]
    [InlineData("read_private_key")]
    [InlineData("get_privatekey")]
    [InlineData("list_ssh_keys")]
    [InlineData("get_auth_key")]
    [InlineData("read_session_key")]
    [InlineData("get_bearer_token")]
    [InlineData("read_cookie")]
    [InlineData("get_certificate")]
    [InlineData("list_vault_items")]
    [InlineData("read_keystore")]
    public void IsReadOnlyToolName_SensitiveTokens_AreNotReadOnly(string toolName)
    {
        // Sensitive substrings must veto the read-only verdict even when the verb prefix
        // (get_, read_, list_, ...) would otherwise classify the tool as safe.
        McpReadOnlyHeuristic.IsReadOnlyToolName(toolName).Should().BeFalse();
    }

    [Theory]
    [InlineData("Redmine/Redmine-list_issues")]
    [InlineData("Redmine/list_issues")]
    [InlineData("Redmine-list_issues")]
    [InlineData("zabbix/get_hosts")]
    public void IsReadOnlyToolName_StripsServerPrefix(string toolName)
    {
        McpReadOnlyHeuristic.IsReadOnlyToolName(toolName).Should().BeTrue();
    }

    [Theory]
    [InlineData("GET_USERS")]
    [InlineData("List_Issues")]
    [InlineData("SeArCh_Things")]
    public void IsReadOnlyToolName_IsCaseInsensitive(string toolName)
    {
        McpReadOnlyHeuristic.IsReadOnlyToolName(toolName).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsReadOnlyToolName_NullOrWhitespace_ReturnsFalse(string? toolName)
    {
        McpReadOnlyHeuristic.IsReadOnlyToolName(toolName).Should().BeFalse();
    }

    [Theory]
    [InlineData("getx_users")]
    [InlineData("listing_things")]
    [InlineData("status_change")]
    [InlineData("findall")]
    public void IsReadOnlyToolName_PrefixWithoutUnderscore_IsNotReadOnly(string toolName)
    {
        // The heuristic only matches "get_*", "list_*", etc. Bare "getx", "listing",
        // and similar strings must not be auto-approved — otherwise an MCP tool named
        // "status_change" or "getconfigurationreset" would slip past the approval
        // prompt in Safe mode.
        McpReadOnlyHeuristic.IsReadOnlyToolName(toolName).Should().BeFalse();
    }
}
