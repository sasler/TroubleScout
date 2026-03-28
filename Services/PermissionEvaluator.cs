using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitHub.Copilot.SDK;

namespace TroubleScout.Services;

internal sealed record ShellPermissionAssessment(string Command, CommandValidation Validation, string PromptReason, string ImpactText);

internal static class PermissionEvaluator
{
    internal static ShellPermissionAssessment? EvaluateShellPermissionRequest(
        PermissionRequest request,
        ExecutionMode executionMode,
        IReadOnlyList<string>? configuredSafeCommands)
    {
        if (NormalizePermissionKind(request.Kind) != "shell")
        {
            return null;
        }

        var fullCommand = TryReadShellCommandText(request, truncateForDisplay: false);
        if (string.IsNullOrWhiteSpace(fullCommand) || !LooksLikePowerShellCommand(fullCommand))
        {
            return null;
        }

        var validation = PowerShellExecutor.ValidateStandaloneCommand(fullCommand, executionMode, configuredSafeCommands);
        return new ShellPermissionAssessment(
            TrimPermissionPreview(fullCommand),
            validation,
            validation.Reason ?? BuildPermissionPromptReason("shell"),
            BuildShellPermissionImpactText(validation));
    }

    internal static string DescribePermissionRequest(PermissionRequest request)
    {
        var kind = NormalizePermissionKind(request.Kind);

        switch (kind)
        {
            case "shell":
            {
                var command = TryReadShellCommandText(request, truncateForDisplay: true);
                return !string.IsNullOrWhiteSpace(command)
                    ? command
                    : "Shell command";
            }
            case "mcp":
            {
                var serverName = request is PermissionRequestMcp mcpRequest
                    ? mcpRequest.ServerName?.Trim()
                    : ReadStringProperty(request, "McpServerName", "ServerName", "Server", "Name");
                var toolName = request is PermissionRequestMcp typedMcp
                    ? typedMcp.ToolName?.Trim() ?? typedMcp.ToolTitle?.Trim()
                    : ReadStringProperty(request, "ToolName", "ToolTitle", "Tool", "Method");
                var arguments = ReadPermissionObjectString(request, "Args", "Arguments", "Params", "Input");

                var target = string.IsNullOrWhiteSpace(serverName)
                    ? toolName
                    : string.IsNullOrWhiteSpace(toolName)
                        ? serverName
                        : $"{serverName}/{toolName}";

                if (!string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(arguments))
                {
                    return TrimPermissionPreview($"{target} {arguments}");
                }

                return !string.IsNullOrWhiteSpace(target)
                    ? target
                    : "MCP tool invocation";
            }
            case "write":
            {
                var path = request is PermissionRequestWrite writeRequest
                    ? writeRequest.FileName?.Trim()
                    : ReadStringProperty(request, "FileName", "Path", "FilePath", "Target", "Uri");
                return !string.IsNullOrWhiteSpace(path)
                    ? $"Write file: {path}"
                    : "File write";
            }
            case "read":
            {
                var path = request is PermissionRequestRead readRequest
                    ? readRequest.Path?.Trim()
                    : ReadStringProperty(request, "Path", "FilePath", "Target", "Uri");
                return !string.IsNullOrWhiteSpace(path)
                    ? $"Read file: {path}"
                    : "File read";
            }
            case "url":
            {
                var url = request is PermissionRequestUrl urlRequest
                    ? urlRequest.Url?.Trim()
                    : ReadStringProperty(request, "Url", "Uri");
                return !string.IsNullOrWhiteSpace(url)
                    ? $"Fetch URL: {url}"
                    : "URL fetch";
            }
            case "custom-tool":
            {
                var toolName = request is PermissionRequestCustomTool customToolRequest
                    ? customToolRequest.ToolName?.Trim()
                    : ReadStringProperty(request, "ToolName", "Tool", "Name");
                var arguments = ReadPermissionObjectString(request, "Args", "Arguments", "Params", "Input");

                if (!string.IsNullOrWhiteSpace(toolName) && !string.IsNullOrWhiteSpace(arguments))
                {
                    return TrimPermissionPreview($"{toolName} {arguments}");
                }

                return !string.IsNullOrWhiteSpace(toolName)
                    ? toolName
                    : "Custom tool invocation";
            }
            default:
            {
                var preview = ReadPermissionObjectString(request, "FullCommandText", "Command", "ToolName", "Path", "Url", "Uri")
                    ?? ReadPermissionObjectString(request, "Args", "Arguments", "Params", "Input");
                return !string.IsNullOrWhiteSpace(preview)
                    ? TrimPermissionPreview(preview)
                    : $"Tool operation ({kind})";
            }
        }
    }

    internal static string BuildPermissionPromptReason(string kind)
    {
        return kind switch
        {
            "mcp" => "Allow this MCP tool invocation in Safe mode?",
            "shell" => "Allow this shell command in Safe mode?",
            "write" => "Allow this file write in Safe mode?",
            "read" => "Allow this file read?",
            "url" => "Allow this URL fetch?",
            "custom-tool" => "Allow this custom tool invocation?",
            _ => $"Allow this tool operation in Safe mode? (kind: {Spectre.Console.Markup.Escape(kind)})"
        };
    }

    internal static string NormalizePermissionKind(string? kind)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "file-read" => "read",
            "file-write" => "write",
            "url-fetch" => "url",
            _ => normalized
        };
    }

    private static string BuildShellPermissionImpactText(CommandValidation validation)
    {
        if (!validation.IsAllowed && !validation.RequiresApproval)
        {
            return "This PowerShell command is blocked by TroubleScout safety rules.";
        }

        if (validation.RequiresApproval)
        {
            if (validation.Reason?.Contains("parse", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "This PowerShell command could not be confidently classified as read-only.";
            }

            return "This PowerShell command is not classified as read-only and may modify system state, services, or configuration.";
        }

        return "This PowerShell command was recognized as read-only.";
    }

    private static string? TryReadShellCommandText(PermissionRequest request, bool truncateForDisplay)
    {
        var command = request is PermissionRequestShell shellRequest
            ? shellRequest.FullCommandText?.Trim()
            : ReadStringProperty(request, "FullCommandText", "Command", "CommandLine")
              ?? ReadPermissionObjectRawString(request,
                  "FullCommandText",
                  "Command",
                  "CommandLine",
                  "CommandText",
                  "Cmd",
                  "ShellCommand",
                  "RawCommand",
                  "Text")
              ?? ReadNestedPermissionObjectRawString(request,
                  "Command",
                  "Payload",
                  "Input",
                  "Request",
                  "Details");

        return string.IsNullOrWhiteSpace(command)
            ? null
            : truncateForDisplay
                ? TrimPermissionPreview(command)
                : command.Trim();
    }

    private static string? ReadPermissionObjectString(object? instance, params string[] propertyNames)
    {
        if (instance == null)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            var text = ConvertPermissionExtensionValueToString(GetPropertyValue(instance, propertyName));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return TrimPermissionPreview(text);
            }
        }

        return null;
    }

    private static string? ReadPermissionObjectRawString(object? instance, params string[] propertyNames)
    {
        if (instance == null)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            var text = ConvertPermissionExtensionValueToRawString(GetPropertyValue(instance, propertyName));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? ReadNestedPermissionObjectRawString(object? instance, params string[] propertyNames)
    {
        if (instance == null)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            var value = GetPropertyValue(instance, propertyName);
            if (value == null)
            {
                continue;
            }

            var text = ExtractNestedRawCommandText(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static bool LooksLikePowerShellCommand(string command)
    {
        var trimmed = command.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith("$", StringComparison.Ordinal) ||
            trimmed.StartsWith("@(", StringComparison.Ordinal) ||
            trimmed.StartsWith("@{", StringComparison.Ordinal) ||
            trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return true;
        }

        var firstCommandSegment = trimmed
            .Split(['|', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .FirstOrDefault();

        var firstToken = firstCommandSegment?
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstToken) || firstToken.StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        return Regex.IsMatch(firstToken, "^[A-Za-z][A-Za-z0-9]*-[A-Za-z][A-Za-z0-9]*$");
    }

    private static string? ReadRawPermissionExtensionString(
        IReadOnlyDictionary<string, object>? extensionData,
        params string[] candidateKeys)
    {
        if (extensionData == null || extensionData.Count == 0)
        {
            return null;
        }

        foreach (var candidateKey in candidateKeys)
        {
            if (!TryGetExtensionValue(extensionData, candidateKey, out var value))
            {
                continue;
            }

            var text = ConvertPermissionExtensionValueToRawString(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? ReadNestedRawPermissionExtensionString(
        IReadOnlyDictionary<string, object>? extensionData,
        params string[] candidateKeys)
    {
        if (extensionData == null || extensionData.Count == 0)
        {
            return null;
        }

        foreach (var candidateKey in candidateKeys)
        {
            if (!TryGetExtensionValue(extensionData, candidateKey, out var value))
            {
                continue;
            }

            if (value == null)
            {
                continue;
            }

            var text = ExtractNestedRawCommandText(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? ReadPermissionExtensionString(
        IReadOnlyDictionary<string, object>? extensionData,
        params string[] candidateKeys)
    {
        if (extensionData == null || extensionData.Count == 0)
        {
            return null;
        }

        foreach (var candidateKey in candidateKeys)
        {
            if (!TryGetExtensionValue(extensionData, candidateKey, out var value))
            {
                continue;
            }

            var text = ConvertPermissionExtensionValueToString(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return TrimPermissionPreview(text);
            }
        }

        return null;
    }

    private static string? ReadNestedPermissionExtensionString(
        IReadOnlyDictionary<string, object>? extensionData,
        params string[] containerKeys)
    {
        if (extensionData == null || extensionData.Count == 0)
        {
            return null;
        }

        foreach (var containerKey in containerKeys)
        {
            if (!TryGetExtensionValue(extensionData, containerKey, out var value) || value == null)
            {
                continue;
            }

            var text = ExtractNestedCommandText(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return TrimPermissionPreview(text);
            }
        }

        return null;
    }

    private static string? ExtractNestedCommandText(object value)
    {
        if (value is JsonElement json)
        {
            return ExtractNestedCommandText(json);
        }

        if (value is IReadOnlyDictionary<string, object> readOnlyDictionary)
        {
            return ReadPermissionExtensionString(readOnlyDictionary,
                "fullCommandText",
                "command",
                "commandLine",
                "commandText",
                "cmd",
                "shellCommand",
                "rawCommand",
                "text");
        }

        if (value is IDictionary<string, object> dictionary)
        {
            return ReadPermissionExtensionString(
                dictionary.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
                "fullCommandText",
                "command",
                "commandLine",
                "commandText",
                "cmd",
                "shellCommand",
                "rawCommand",
                "text");
        }

        return null;
    }

    private static string? ExtractNestedRawCommandText(object value)
    {
        if (value is JsonElement json)
        {
            return ExtractNestedRawCommandText(json);
        }

        if (value is IReadOnlyDictionary<string, object> readOnlyDictionary)
        {
            return ReadRawPermissionExtensionString(readOnlyDictionary,
                "fullCommandText",
                "command",
                "commandLine",
                "commandText",
                "cmd",
                "shellCommand",
                "rawCommand",
                "text");
        }

        if (value is IDictionary<string, object> dictionary)
        {
            return ReadRawPermissionExtensionString(
                dictionary.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
                "fullCommandText",
                "command",
                "commandLine",
                "commandText",
                "cmd",
                "shellCommand",
                "rawCommand",
                "text");
        }

        return null;
    }

    private static string? ExtractNestedCommandText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return TrimSingleLine(element.GetString());
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[]
                 {
                     "fullCommandText", "command", "commandLine", "commandText", "cmd", "shellCommand", "rawCommand", "text"
                 })
        {
            if (JsonParsingHelpers.TryGetJsonPropertyIgnoreCase(element, propertyName, out var value))
            {
                var text = ExtractNestedCommandText(value);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static string? ExtractNestedRawCommandText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[]
                 {
                     "fullCommandText", "command", "commandLine", "commandText", "cmd", "shellCommand", "rawCommand", "text"
                 })
        {
            if (JsonParsingHelpers.TryGetJsonPropertyIgnoreCase(element, propertyName, out var value))
            {
                var text = ExtractNestedRawCommandText(value);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static bool TryGetExtensionValue(
        IReadOnlyDictionary<string, object> extensionData,
        string candidateKey,
        out object? value)
    {
        foreach (var entry in extensionData)
        {
            if (entry.Key.Equals(candidateKey, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? ConvertPermissionExtensionValueToString(object? value)
    {
        string? rawText = value switch
        {
            null => null,
            string text => text,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            JsonElement json => json.GetRawText(),
            _ => value.ToString()
        };

        return string.IsNullOrWhiteSpace(rawText)
            ? null
            : TrimSingleLine(rawText);
    }

    private static string? ConvertPermissionExtensionValueToRawString(object? value)
    {
        string? rawText = value switch
        {
            null => null,
            string text => text,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            JsonElement json => ExtractNestedRawCommandText(json) ?? json.GetRawText(),
            _ => value.ToString()
        };

        return string.IsNullOrWhiteSpace(rawText)
            ? null
            : rawText.Trim();
    }

    private static string? ReadStringProperty(object? instance, params string[] propertyNames)
    {
        if (instance == null)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            var prop = instance.GetType().GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var value = prop?.GetValue(instance);
            if (value is string stringValue && !string.IsNullOrWhiteSpace(stringValue))
            {
                return stringValue.Trim();
            }
        }

        return null;
    }

    private static object? GetPropertyValue(object instance, string propertyName)
    {
        var prop = instance.GetType().GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return prop?.GetValue(instance);
    }

    private static string TrimSingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Unknown error";
        }

        var trimmed = text.Trim();
        var newlineIndex = trimmed.IndexOfAny(['\r', '\n']);
        return newlineIndex < 0 ? trimmed : trimmed[..newlineIndex].Trim();
    }

    private static string TrimPermissionPreview(string text)
    {
        const int maxLength = 180;

        var singleLine = Regex.Replace(text.Trim(), @"\s*[\r\n]+\s*", " ");
        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..maxLength].TrimEnd() + "...";
    }
}
