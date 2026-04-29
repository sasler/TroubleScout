using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TroubleScout.Services;

internal sealed record ReportPromptEntry(DateTimeOffset Timestamp, string Prompt, List<ReportActionEntry> Actions, string AgentReply)
{
    public TroubleScout.UI.StatusBarInfo? StatusBar { get; init; }
}

    internal sealed record ReportActionEntry(
        DateTimeOffset Timestamp,
        string Target,
        string Command,
        string Output,
        string SafetyApproval,
        string Source)
    {
        public string? Arguments { get; init; }
        public bool? Success { get; init; }
        public string? ToolCallId { get; init; }
    }

internal sealed record ReportSessionSummary(
    string CurrentModel,
    string CurrentProvider,
    IReadOnlyList<string> ModelsUsed,
    IReadOnlyList<string> ConfiguredMcpServers,
    IReadOnlyList<string> UsedMcpServers,
    string? MonitoringMcp,
    string? TicketingMcp,
    IReadOnlyList<string> ApprovedMcpServersForSession,
    IReadOnlyList<string> PersistedApprovedMcpServers,
    IReadOnlyList<string> ConfiguredSkills,
    IReadOnlyList<string> UsedSkills,
    string ExecutionMode,
    string TargetServer);

internal static class ReportHtmlBuilder
{
    internal static string BuildReportHtml(IReadOnlyList<ReportPromptEntry> prompts) =>
        BuildReportHtml(prompts, summary: null);

    internal static string BuildReportHtml(IReadOnlyList<ReportPromptEntry> prompts, ReportSessionSummary? summary)
    {
        var totalActions = prompts.Sum(prompt => prompt.Actions.Count);
        var generatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

        // Compute session duration from first to last prompt
        var sessionDuration = string.Empty;
        if (prompts.Count >= 2)
        {
            var span = prompts[^1].Timestamp - prompts[0].Timestamp;
            sessionDuration = span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss")
                : span.ToString(@"m\:ss");
        }
        else
        {
            sessionDuration = "N/A";
        }

        // Compute approval breakdown
        var safeCount = 0;
        var approvedCount = 0;
        var blockedCount = 0;
        var deniedCount = 0;
        foreach (var p in prompts)
        {
            foreach (var a in p.Actions)
            {
                switch (a.SafetyApproval)
                {
                    case "SafeAuto":
                        safeCount++;
                        break;
                    case "AutoApprovedYolo":
                    case "ApprovedByUser":
                        approvedCount++;
                        break;
                    case "Blocked":
                        blockedCount++;
                        break;
                    case "ApprovalRequested":
                        // ApprovalRequested is an intermediate state that is later followed by ApprovedByUser or Denied.
                        break;
                    case "Denied":
                        deniedCount++;
                        break;
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\" />");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        sb.AppendLine("  <title>TroubleScout Session Report</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    *, *::before, *::after { box-sizing: border-box; }");
        sb.AppendLine("    :root { color-scheme: dark; }");
        sb.AppendLine("    body { font-family: 'Segoe UI', Inter, system-ui, -apple-system, sans-serif; margin: 0; background: #0b1121; color: #e2e8f0; line-height: 1.6; -webkit-font-smoothing: antialiased; }");
        sb.AppendLine("    .wrap { max-width: 1100px; margin: 0 auto; padding: 24px 20px 48px; }");

        // Hero header
        sb.AppendLine("    .hero { background: linear-gradient(135deg, #1e293b 0%, #0f172a 50%, #1a1c2e 100%); border: 1px solid #334155; border-radius: 16px; padding: 32px 36px 28px; margin-bottom: 24px; position: relative; overflow: hidden; }");
        sb.AppendLine("    .hero::before { content: ''; position: absolute; top: -50%; right: -20%; width: 400px; height: 400px; background: radial-gradient(circle, rgba(59,130,246,0.08) 0%, transparent 70%); pointer-events: none; }");
        sb.AppendLine("    .hero-top { display: flex; align-items: center; gap: 16px; margin-bottom: 20px; }");
        sb.AppendLine("    .hero-icon { flex-shrink: 0; }");
        sb.AppendLine("    .hero h1 { margin: 0; font-size: 2rem; font-weight: 700; letter-spacing: -0.02em; color: #f8fafc; }");
        sb.AppendLine("    .hero-subtitle { color: #94a3b8; font-size: 0.95rem; margin-top: 2px; }");
        sb.AppendLine("    .hero-stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 12px; margin-top: 20px; }");
        sb.AppendLine("    .hero-stat { background: rgba(255,255,255,0.04); border: 1px solid #334155; border-radius: 10px; padding: 12px 16px; text-align: center; }");
        sb.AppendLine("    .hero-stat-value { font-size: 1.5rem; font-weight: 700; color: #f8fafc; }");
        sb.AppendLine("    .hero-stat-label { font-size: 0.78rem; color: #94a3b8; text-transform: uppercase; letter-spacing: 0.05em; margin-top: 2px; }");

        // Summary cards
        sb.AppendLine("    .summary-row { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 12px; margin-bottom: 28px; }");
        sb.AppendLine("    .summary-card { border-radius: 12px; padding: 16px 18px; border: 1px solid; }");
        sb.AppendLine("    .summary-card .sc-val { font-size: 1.75rem; font-weight: 700; }");
        sb.AppendLine("    .summary-card .sc-lbl { font-size: 0.8rem; text-transform: uppercase; letter-spacing: 0.04em; opacity: 0.85; margin-top: 2px; }");
        sb.AppendLine("    .sc-green { background: rgba(34,197,94,0.08); border-color: rgba(34,197,94,0.25); color: #22c55e; }");
        sb.AppendLine("    .sc-blue { background: rgba(59,130,246,0.08); border-color: rgba(59,130,246,0.25); color: #3b82f6; }");
        sb.AppendLine("    .sc-red { background: rgba(239,68,68,0.08); border-color: rgba(239,68,68,0.25); color: #ef4444; }");
        sb.AppendLine("    .sc-amber { background: rgba(245,158,11,0.08); border-color: rgba(245,158,11,0.25); color: #f59e0b; }");

        // Timeline
        sb.AppendLine("    .timeline { position: relative; padding-left: 36px; }");
        sb.AppendLine("    .timeline::before { content: ''; position: absolute; left: 15px; top: 0; bottom: 0; width: 2px; background: linear-gradient(to bottom, #334155, #1e293b); }");
        sb.AppendLine("    .timeline-item { position: relative; margin-bottom: 20px; }");
        sb.AppendLine("    .timeline-dot { position: absolute; left: -29px; top: 18px; width: 12px; height: 12px; border-radius: 50%; background: #3b82f6; border: 2px solid #0b1121; z-index: 1; }");

        // Prompt card
        sb.AppendLine("    .prompt-card { background: #111827; border: 1px solid #1e293b; border-radius: 14px; overflow: hidden; transition: border-color 0.2s ease; }");
        sb.AppendLine("    .prompt-card:hover { border-color: #334155; }");
        sb.AppendLine("    .prompt-header { padding: 18px 20px; cursor: pointer; display: flex; align-items: flex-start; gap: 14px; }");
        sb.AppendLine("    .prompt-header:hover { background: rgba(255,255,255,0.02); }");
        sb.AppendLine("    .prompt-badge { flex-shrink: 0; width: 36px; height: 36px; border-radius: 10px; background: linear-gradient(135deg, #3b82f6, #6366f1); display: flex; align-items: center; justify-content: center; font-weight: 700; font-size: 0.9rem; color: #fff; }");
        sb.AppendLine("    .prompt-info { flex: 1; min-width: 0; }");
        sb.AppendLine("    .prompt-text { font-size: 1.05rem; font-weight: 600; color: #f1f5f9; word-break: break-word; }");
        sb.AppendLine("    .prompt-meta { display: flex; flex-wrap: wrap; gap: 12px; margin-top: 6px; font-size: 0.82rem; color: #94a3b8; }");
        sb.AppendLine("    .prompt-meta-item { display: flex; align-items: center; gap: 4px; }");
        sb.AppendLine("    .prompt-chevron { flex-shrink: 0; width: 20px; height: 20px; color: #64748b; transition: transform 0.25s ease; margin-top: 8px; }");
        sb.AppendLine("    details[open] > .prompt-header .prompt-chevron { transform: rotate(90deg); }");

        // Prompt card details/summary native reset
        sb.AppendLine("    .prompt-card > summary { list-style: none; }");
        sb.AppendLine("    .prompt-card > summary::-webkit-details-marker { display: none; }");
        sb.AppendLine("    .prompt-card > summary::marker { display: none; content: ''; }");

        // Prompt body with expand/collapse transition
        sb.AppendLine("    .prompt-body { padding: 0 20px 20px; border-top: 1px solid #1e293b; }");

        // Action card
        sb.AppendLine("    .action-card { background: #0d1525; border: 1px solid #1e293b; border-radius: 10px; padding: 16px; margin-top: 14px; }");
        sb.AppendLine("    .action-header { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; margin-bottom: 12px; }");
        sb.AppendLine("    .action-time { font-size: 0.82rem; color: #94a3b8; font-family: 'Cascadia Mono', Consolas, monospace; }");
        sb.AppendLine("    .action-target { font-size: 0.82rem; color: #cbd5e1; background: rgba(255,255,255,0.05); padding: 2px 10px; border-radius: 6px; }");

        // Approval chips
        sb.AppendLine("    .approval-chip { display: inline-flex; align-items: center; gap: 5px; padding: 3px 10px; border-radius: 999px; font-size: 0.78rem; font-weight: 600; }");
        sb.AppendLine("    .approval-SafeAuto { background: rgba(34,197,94,0.12); color: #22c55e; border: 1px solid rgba(34,197,94,0.3); }");
        sb.AppendLine("    .approval-AutoApprovedYolo { background: rgba(249,115,22,0.12); color: #f97316; border: 1px solid rgba(249,115,22,0.3); }");
        sb.AppendLine("    .approval-ApprovedByUser { background: rgba(59,130,246,0.12); color: #3b82f6; border: 1px solid rgba(59,130,246,0.3); }");
        sb.AppendLine("    .approval-ApprovalRequested { background: rgba(234,179,8,0.12); color: #eab308; border: 1px solid rgba(234,179,8,0.3); }");
        sb.AppendLine("    .approval-Denied { background: rgba(239,68,68,0.12); color: #ef4444; border: 1px solid rgba(239,68,68,0.3); }");
        sb.AppendLine("    .approval-Blocked { background: rgba(220,38,38,0.12); color: #dc2626; border: 1px solid rgba(220,38,38,0.3); }");
        sb.AppendLine("    .source-chip { display: inline-block; padding: 2px 9px; border-radius: 6px; font-size: 0.78rem; color: #94a3b8; background: rgba(255,255,255,0.05); border: 1px solid #334155; }");

        // Inner expandable sections
        sb.AppendLine("    .inner-section { margin-top: 10px; border: 1px solid #1e293b; border-radius: 8px; overflow: hidden; background: #0a1223; }");
        sb.AppendLine("    .inner-section > summary { list-style: none; padding: 10px 14px; font-size: 0.82rem; font-weight: 600; letter-spacing: 0.03em; color: #93c5fd; text-transform: uppercase; cursor: pointer; display: flex; align-items: center; gap: 8px; }");
        sb.AppendLine("    .inner-section > summary::-webkit-details-marker { display: none; }");
        sb.AppendLine("    .inner-section > summary::marker { display: none; content: ''; }");
        sb.AppendLine("    .inner-section > summary:hover { background: rgba(255,255,255,0.02); }");
        sb.AppendLine("    .inner-section > summary::before { content: '\\25B6'; font-size: 0.6rem; transition: transform 0.2s ease; display: inline-block; }");
        sb.AppendLine("    .inner-section[open] > summary::before { transform: rotate(90deg); }");
        sb.AppendLine("    .inner-content { padding: 12px 14px; }");

        // Code blocks with line numbers and copy button
        sb.AppendLine("    .code-wrap { position: relative; margin: 0; }");
        sb.AppendLine("    .copy-btn { position: absolute; top: 8px; right: 8px; background: rgba(255,255,255,0.08); border: 1px solid #334155; border-radius: 6px; color: #94a3b8; font-size: 0.75rem; padding: 4px 10px; cursor: pointer; transition: all 0.15s ease; z-index: 2; font-family: inherit; }");
        sb.AppendLine("    .copy-btn:hover { background: rgba(255,255,255,0.14); color: #e2e8f0; }");
        sb.AppendLine("    .copy-btn.copied { background: rgba(34,197,94,0.15); border-color: rgba(34,197,94,0.3); color: #22c55e; }");
        sb.AppendLine("    .code-block { margin: 0; white-space: pre-wrap; word-break: break-word; font-family: 'Cascadia Mono', Consolas, 'Courier New', monospace; font-size: 0.88rem; line-height: 1.6; border: 1px solid #1e293b; border-radius: 8px; padding: 12px 14px 12px 0; background: #080e1c; counter-reset: line; overflow-x: auto; }");
        sb.AppendLine("    .code-line { display: block; padding-left: 52px; position: relative; min-height: 1.6em; }");
        sb.AppendLine("    .code-line::before { counter-increment: line; content: counter(line); position: absolute; left: 0; width: 40px; text-align: right; color: #334155; font-size: 0.78rem; padding-right: 12px; user-select: none; -webkit-user-select: none; }");
        sb.AppendLine("    .output-block { margin: 0; white-space: pre-wrap; word-break: break-word; font-family: 'Cascadia Mono', Consolas, 'Courier New', monospace; font-size: 0.85rem; line-height: 1.5; border: 1px solid #1e293b; border-radius: 8px; padding: 12px 14px; background: #080e1c; max-height: 400px; overflow-y: auto; }");

        // Syntax highlighting tokens
        sb.AppendLine("    .tok-cmdlet { color: #67e8f9; font-weight: 600; }");
        sb.AppendLine("    .tok-param { color: #fde68a; }");
        sb.AppendLine("    .tok-string { color: #86efac; }");
        sb.AppendLine("    .tok-variable { color: #93c5fd; }");
        sb.AppendLine("    .tok-number { color: #c4b5fd; }");
        sb.AppendLine("    .tok-op { color: #f9a8d4; }");

        // Agent reply chat bubble
        sb.AppendLine("    .reply-bubble { margin-top: 16px; background: linear-gradient(135deg, #162032, #111827); border: 1px solid #1e293b; border-radius: 14px; padding: 18px 20px; position: relative; }");
        sb.AppendLine("    .reply-header { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; }");
        sb.AppendLine("    .reply-avatar { width: 32px; height: 32px; border-radius: 8px; background: linear-gradient(135deg, #6366f1, #8b5cf6); display: flex; align-items: center; justify-content: center; flex-shrink: 0; }");
        sb.AppendLine("    .reply-label { font-size: 0.85rem; font-weight: 600; color: #a5b4fc; }");
        sb.AppendLine("    .reply-text { white-space: pre-wrap; word-break: break-word; font-size: 0.92rem; line-height: 1.65; color: #cbd5e1; }");

        // Muted text
        sb.AppendLine("    .muted { color: #64748b; font-size: 0.88rem; }");

        // No actions
        sb.AppendLine("    .no-actions { padding: 16px; text-align: center; color: #64748b; font-style: italic; }");

        // Status bar (mirrors terminal)
        sb.AppendLine("    .statusbar { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 12px; padding-top: 12px; border-top: 1px dashed #1e293b; font-size: 0.78rem; }");
        sb.AppendLine("    .sb-chip { display: inline-flex; align-items: center; gap: 4px; padding: 3px 9px; border-radius: 999px; background: rgba(255,255,255,0.04); border: 1px solid #334155; color: #94a3b8; font-family: 'Cascadia Mono', Consolas, monospace; }");
        sb.AppendLine("    .sb-chip strong { color: #e2e8f0; font-weight: 600; }");
        sb.AppendLine("    .sb-chip.sb-model { color: #67e8f9; border-color: rgba(103,232,249,0.3); }");
        sb.AppendLine("    .sb-chip.sb-tokens { color: #a5b4fc; border-color: rgba(165,180,252,0.3); }");
        sb.AppendLine("    .sb-chip.sb-tools { color: #fcd34d; border-color: rgba(252,211,77,0.3); }");

        // MCP server chip
        sb.AppendLine("    .mcp-chip { display: inline-flex; align-items: center; gap: 5px; padding: 3px 10px; border-radius: 6px; font-size: 0.78rem; font-weight: 600; background: rgba(168,85,247,0.12); color: #c084fc; border: 1px solid rgba(168,85,247,0.3); }");
        sb.AppendLine("    .approval-MCP { background: rgba(168,85,247,0.12); color: #c084fc; border: 1px solid rgba(168,85,247,0.3); }");
        sb.AppendLine("    .approval-Failed { background: rgba(239,68,68,0.12); color: #ef4444; border: 1px solid rgba(239,68,68,0.3); }");

        // Markdown-rendered reply
        sb.AppendLine("    .reply-markdown { font-size: 0.95rem; line-height: 1.7; color: #cbd5e1; }");
        sb.AppendLine("    .reply-markdown h1, .reply-markdown h2, .reply-markdown h3, .reply-markdown h4 { color: #f1f5f9; margin: 1.1em 0 0.5em; line-height: 1.3; }");
        sb.AppendLine("    .reply-markdown h1 { font-size: 1.4rem; border-bottom: 1px solid #334155; padding-bottom: 0.3em; }");
        sb.AppendLine("    .reply-markdown h2 { font-size: 1.2rem; }");
        sb.AppendLine("    .reply-markdown h3 { font-size: 1.05rem; }");
        sb.AppendLine("    .reply-markdown h4 { font-size: 0.95rem; color: #93c5fd; }");
        sb.AppendLine("    .reply-markdown p { margin: 0.6em 0; }");
        sb.AppendLine("    .reply-markdown code { background: rgba(255,255,255,0.06); border: 1px solid #1e293b; border-radius: 4px; padding: 1px 6px; font-family: 'Cascadia Mono', Consolas, monospace; font-size: 0.86em; color: #fde68a; }");
        sb.AppendLine("    .reply-markdown pre { background: #080e1c; border: 1px solid #1e293b; border-radius: 8px; padding: 12px 14px; overflow-x: auto; }");
        sb.AppendLine("    .reply-markdown pre code { background: transparent; border: none; padding: 0; color: #e2e8f0; font-size: 0.88rem; }");
        sb.AppendLine("    .reply-markdown a { color: #60a5fa; text-decoration: none; }");
        sb.AppendLine("    .reply-markdown a:hover { text-decoration: underline; }");
        sb.AppendLine("    .reply-markdown ul, .reply-markdown ol { padding-left: 1.6em; margin: 0.5em 0; }");
        sb.AppendLine("    .reply-markdown li { margin: 0.25em 0; }");
        sb.AppendLine("    .reply-markdown blockquote { margin: 0.6em 0; padding: 0.4em 1em; border-left: 3px solid #475569; color: #94a3b8; background: rgba(255,255,255,0.02); border-radius: 0 6px 6px 0; }");
        sb.AppendLine("    .reply-markdown table { border-collapse: collapse; margin: 0.7em 0; font-size: 0.88rem; }");
        sb.AppendLine("    .reply-markdown th, .reply-markdown td { border: 1px solid #334155; padding: 6px 10px; }");
        sb.AppendLine("    .reply-markdown th { background: rgba(255,255,255,0.04); }");
        sb.AppendLine("    .reply-markdown hr { border: none; border-top: 1px solid #334155; margin: 1em 0; }");
        sb.AppendLine("    .reply-actions { display: flex; gap: 8px; margin-top: 12px; }");
        sb.AppendLine("    .reply-action-btn { background: rgba(255,255,255,0.05); border: 1px solid #334155; color: #94a3b8; font-size: 0.78rem; padding: 4px 10px; border-radius: 6px; cursor: pointer; font-family: inherit; transition: all 0.15s ease; }");
        sb.AppendLine("    .reply-action-btn:hover { background: rgba(255,255,255,0.1); color: #e2e8f0; }");
        sb.AppendLine("    .reply-action-btn.copied { background: rgba(34,197,94,0.15); border-color: rgba(34,197,94,0.3); color: #22c55e; }");

        // Top action bar
        sb.AppendLine("    .top-bar { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; justify-content: flex-end; margin-bottom: 12px; }");
        sb.AppendLine("    .top-btn { background: rgba(255,255,255,0.04); border: 1px solid #334155; color: #cbd5e1; font-size: 0.85rem; padding: 6px 14px; border-radius: 8px; cursor: pointer; font-family: inherit; transition: all 0.15s ease; display: inline-flex; align-items: center; gap: 6px; }");
        sb.AppendLine("    .top-btn:hover { background: rgba(255,255,255,0.1); }");

        // Session header card
        sb.AppendLine("    .session-header { background: #111827; border: 1px solid #1e293b; border-radius: 12px; padding: 18px 22px; margin-bottom: 20px; }");
        sb.AppendLine("    .session-header h2 { margin: 0 0 12px; font-size: 1rem; color: #93c5fd; text-transform: uppercase; letter-spacing: 0.05em; }");
        sb.AppendLine("    .session-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 12px 24px; font-size: 0.88rem; }");
        sb.AppendLine("    .session-grid dt { color: #94a3b8; font-size: 0.78rem; text-transform: uppercase; letter-spacing: 0.04em; margin-bottom: 2px; }");
        sb.AppendLine("    .session-grid dd { margin: 0 0 4px; color: #e2e8f0; word-break: break-word; }");

        // TOC
        sb.AppendLine("    .toc { position: fixed; top: 80px; right: 16px; max-width: 240px; max-height: calc(100vh - 120px); overflow-y: auto; background: #111827; border: 1px solid #1e293b; border-radius: 10px; padding: 12px 14px; font-size: 0.82rem; z-index: 50; }");
        sb.AppendLine("    .toc h3 { margin: 0 0 8px; font-size: 0.78rem; color: #93c5fd; text-transform: uppercase; letter-spacing: 0.05em; }");
        sb.AppendLine("    .toc ol { padding-left: 18px; margin: 0; }");
        sb.AppendLine("    .toc li { margin: 3px 0; }");
        sb.AppendLine("    .toc a { color: #cbd5e1; text-decoration: none; }");
        sb.AppendLine("    .toc a:hover { color: #60a5fa; }");
        sb.AppendLine("    @media (max-width: 1400px) { .toc { display: none; } }");

        // Light theme overrides
        sb.AppendLine("    [data-theme=\"light\"] { color-scheme: light; }");
        sb.AppendLine("    [data-theme=\"light\"] body { background: #f8fafc; color: #1e293b; }");
        sb.AppendLine("    [data-theme=\"light\"] .hero { background: linear-gradient(135deg, #e0f2fe 0%, #f1f5f9 100%); border-color: #cbd5e1; }");
        sb.AppendLine("    [data-theme=\"light\"] .hero h1, [data-theme=\"light\"] .hero-stat-value { color: #0f172a; }");
        sb.AppendLine("    [data-theme=\"light\"] .hero-subtitle, [data-theme=\"light\"] .hero-stat-label { color: #475569; }");
        sb.AppendLine("    [data-theme=\"light\"] .hero-stat { background: rgba(255,255,255,0.6); border-color: #cbd5e1; }");
        sb.AppendLine("    [data-theme=\"light\"] .prompt-card, [data-theme=\"light\"] .session-header, [data-theme=\"light\"] .toc { background: #ffffff; border-color: #cbd5e1; }");
        sb.AppendLine("    [data-theme=\"light\"] .prompt-text { color: #0f172a; }");
        sb.AppendLine("    [data-theme=\"light\"] .prompt-meta, [data-theme=\"light\"] .muted, [data-theme=\"light\"] .session-grid dt, [data-theme=\"light\"] .toc h3 { color: #475569; }");
        sb.AppendLine("    [data-theme=\"light\"] .prompt-card:hover, [data-theme=\"light\"] .prompt-header:hover { background: #f1f5f9; border-color: #cbd5e1; }");
        sb.AppendLine("    [data-theme=\"light\"] .action-card { background: #f8fafc; border-color: #e2e8f0; }");
        sb.AppendLine("    [data-theme=\"light\"] .inner-section { background: #ffffff; border-color: #e2e8f0; }");
        sb.AppendLine("    [data-theme=\"light\"] .code-block, [data-theme=\"light\"] .output-block, [data-theme=\"light\"] .reply-markdown pre { background: #f1f5f9; border-color: #cbd5e1; color: #1e293b; }");
        sb.AppendLine("    [data-theme=\"light\"] .reply-bubble { background: linear-gradient(135deg, #eff6ff, #f8fafc); border-color: #cbd5e1; }");
        sb.AppendLine("    [data-theme=\"light\"] .reply-markdown { color: #1e293b; }");
        sb.AppendLine("    [data-theme=\"light\"] .reply-markdown h1, [data-theme=\"light\"] .reply-markdown h2, [data-theme=\"light\"] .reply-markdown h3 { color: #0f172a; }");
        sb.AppendLine("    [data-theme=\"light\"] .reply-markdown code { background: rgba(0,0,0,0.05); border-color: #cbd5e1; color: #b45309; }");
        sb.AppendLine("    [data-theme=\"light\"] .reply-label { color: #4f46e5; }");
        sb.AppendLine("    [data-theme=\"light\"] .top-btn, [data-theme=\"light\"] .reply-action-btn, [data-theme=\"light\"] .copy-btn, [data-theme=\"light\"] .sb-chip { background: rgba(0,0,0,0.04); border-color: #cbd5e1; color: #1e293b; }");
        sb.AppendLine("    [data-theme=\"light\"] .session-grid dd, [data-theme=\"light\"] .toc a { color: #1e293b; }");
        sb.AppendLine("    [data-theme=\"light\"] .footer { color: #94a3b8; border-color: #cbd5e1; }");

        // Footer
        sb.AppendLine("    .footer { margin-top: 40px; padding: 20px 0; border-top: 1px solid #1e293b; text-align: center; color: #475569; font-size: 0.82rem; }");
        sb.AppendLine("    .footer a { color: #64748b; text-decoration: none; }");

        // Print styles
        sb.AppendLine("    @media print {");
        sb.AppendLine("      body { background: #fff; color: #1e293b; -webkit-print-color-adjust: exact; print-color-adjust: exact; }");
        sb.AppendLine("      .hero { background: #f8fafc; border-color: #d1d5db; }");
        sb.AppendLine("      .hero h1, .hero-stat-value { color: #111827; }");
        sb.AppendLine("      .hero-subtitle, .hero-stat-label { color: #6b7280; }");
        sb.AppendLine("      .hero::before { display: none; }");
        sb.AppendLine("      .hero-stat { background: #f1f5f9; border-color: #d1d5db; }");
        sb.AppendLine("      .summary-card { border-width: 2px; }");
        sb.AppendLine("      .prompt-card { background: #fff; border-color: #d1d5db; break-inside: avoid; }");
        sb.AppendLine("      .prompt-card .prompt-body { display: block; }");
        sb.AppendLine("      .inner-section > .inner-content { display: block; }");
        sb.AppendLine("      .prompt-text { color: #111827; }");
        sb.AppendLine("      .prompt-meta { color: #6b7280; }");
        sb.AppendLine("      .prompt-chevron { display: none; }");
        sb.AppendLine("      .action-card { background: #f8fafc; border-color: #d1d5db; break-inside: avoid; }");
        sb.AppendLine("      .inner-section { background: #f8fafc; border-color: #d1d5db; }");
        sb.AppendLine("      .inner-section > summary { color: #3b82f6; }");
        sb.AppendLine("      .code-block, .output-block { background: #f1f5f9; border-color: #d1d5db; color: #1e293b; }");
        sb.AppendLine("      .code-line::before { color: #9ca3af; }");
        sb.AppendLine("      .tok-cmdlet { color: #0369a1; }");
        sb.AppendLine("      .tok-param { color: #92400e; }");
        sb.AppendLine("      .tok-string { color: #166534; }");
        sb.AppendLine("      .tok-variable { color: #1d4ed8; }");
        sb.AppendLine("      .tok-number { color: #7e22ce; }");
        sb.AppendLine("      .tok-op { color: #be185d; }");
        sb.AppendLine("      .reply-bubble { background: #f8fafc; border-color: #d1d5db; }");
        sb.AppendLine("      .reply-label { color: #4f46e5; }");
        sb.AppendLine("      .reply-text { color: #374151; }");
        sb.AppendLine("      .copy-btn { display: none; }");
        sb.AppendLine("      .timeline::before { background: #d1d5db; }");
        sb.AppendLine("      .timeline-dot { background: #3b82f6; border-color: #fff; }");
        sb.AppendLine("      .footer { color: #9ca3af; border-color: #d1d5db; }");
        sb.AppendLine("    }");

        // Responsive
        sb.AppendLine("    @media (max-width: 640px) {");
        sb.AppendLine("      .wrap { padding: 12px 10px 32px; }");
        sb.AppendLine("      .hero { padding: 20px 16px; border-radius: 12px; }");
        sb.AppendLine("      .hero h1 { font-size: 1.4rem; }");
        sb.AppendLine("      .hero-stats { grid-template-columns: repeat(2, 1fr); }");
        sb.AppendLine("      .summary-row { grid-template-columns: repeat(2, 1fr); }");
        sb.AppendLine("      .timeline { padding-left: 0; }");
        sb.AppendLine("      .timeline::before { display: none; }");
        sb.AppendLine("      .timeline-dot { display: none; }");
        sb.AppendLine("      .prompt-header { padding: 14px 14px; }");
        sb.AppendLine("      .prompt-badge { width: 30px; height: 30px; font-size: 0.8rem; }");
        sb.AppendLine("      .prompt-body { padding: 0 14px 14px; }");
        sb.AppendLine("      .action-card { padding: 12px; }");
        sb.AppendLine("    }");

        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"wrap\">");

        // ── Top action bar (theme + export) ──
        sb.AppendLine("    <div class=\"top-bar\">");
        sb.AppendLine("      <button class=\"top-btn\" id=\"export-md-btn\" type=\"button\" title=\"Download full session as Markdown\">&#128194; Export .md</button>");
        sb.AppendLine("      <button class=\"top-btn\" id=\"theme-toggle-btn\" type=\"button\" title=\"Toggle light/dark theme\">&#9788; Theme</button>");
        sb.AppendLine("    </div>");

        // ── Hero header ──
        sb.AppendLine("    <div class=\"hero\">");
        sb.AppendLine("      <div class=\"hero-top\">");
        sb.AppendLine("        <div class=\"hero-icon\">");
        sb.AppendLine("          <svg width=\"44\" height=\"44\" viewBox=\"0 0 44 44\" fill=\"none\" xmlns=\"http://www.w3.org/2000/svg\">");
        sb.AppendLine("            <path d=\"M22 4L38 12V28C38 34.6 30.8 40 22 42C13.2 40 6 34.6 6 28V12L22 4Z\" fill=\"url(#shieldGrad)\" stroke=\"#3b82f6\" stroke-width=\"1.5\" />");
        sb.AppendLine("            <path d=\"M16 22L20 26L28 18\" stroke=\"#f8fafc\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\" />");
        sb.AppendLine("            <defs><linearGradient id=\"shieldGrad\" x1=\"6\" y1=\"4\" x2=\"38\" y2=\"42\" gradientUnits=\"userSpaceOnUse\"><stop stop-color=\"#3b82f6\" stop-opacity=\"0.3\" /><stop offset=\"1\" stop-color=\"#6366f1\" stop-opacity=\"0.15\" /></linearGradient></defs>");
        sb.AppendLine("          </svg>");
        sb.AppendLine("        </div>");
        sb.AppendLine("        <div>");
        sb.AppendLine("          <h1>TroubleScout</h1>");
        sb.AppendLine("          <div class=\"hero-subtitle\">Session Report</div>");
        sb.AppendLine("        </div>");
        sb.AppendLine("      </div>");
        sb.AppendLine("      <div class=\"hero-stats\">");
        sb.AppendLine($"        <div class=\"hero-stat\"><div class=\"hero-stat-value\">{prompts.Count}</div><div class=\"hero-stat-label\">Prompts</div></div>");
        sb.AppendLine($"        <div class=\"hero-stat\"><div class=\"hero-stat-value\">{totalActions}</div><div class=\"hero-stat-label\">Actions</div></div>");
        sb.AppendLine($"        <div class=\"hero-stat\"><div class=\"hero-stat-value\">{HtmlEncode(sessionDuration)}</div><div class=\"hero-stat-label\">Duration</div></div>");
        sb.AppendLine($"        <div class=\"hero-stat\"><div class=\"hero-stat-value\" style=\"font-size:0.95rem\">{HtmlEncode(generatedAt)}</div><div class=\"hero-stat-label\">Generated</div></div>");
        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");

        // ── Session header card (model, mcp, etc.) ──
        if (summary != null)
        {
            sb.AppendLine("    <div class=\"session-header\">");
            sb.AppendLine("      <h2>Session</h2>");
            sb.AppendLine("      <dl class=\"session-grid\">");
            sb.AppendLine($"        <div><dt>Model</dt><dd>{HtmlEncode(summary.CurrentModel)}</dd></div>");
            sb.AppendLine($"        <div><dt>Provider</dt><dd>{HtmlEncode(summary.CurrentProvider)}</dd></div>");
            sb.AppendLine($"        <div><dt>Target</dt><dd>{HtmlEncode(summary.TargetServer)}</dd></div>");
            sb.AppendLine($"        <div><dt>Execution mode</dt><dd>{HtmlEncode(summary.ExecutionMode)}</dd></div>");
            if (summary.UsedMcpServers.Count > 0)
            {
                sb.AppendLine($"        <div><dt>MCP servers used</dt><dd>{HtmlEncode(string.Join(", ", summary.UsedMcpServers))}</dd></div>");
            }
            else if (summary.ConfiguredMcpServers.Count > 0)
            {
                sb.AppendLine($"        <div><dt>MCP servers configured</dt><dd>{HtmlEncode(string.Join(", ", summary.ConfiguredMcpServers))}</dd></div>");
            }
            if (!string.IsNullOrWhiteSpace(summary.MonitoringMcp))
            {
                sb.AppendLine($"        <div><dt>Monitoring MCP</dt><dd>{HtmlEncode(summary.MonitoringMcp!)}</dd></div>");
            }
            if (!string.IsNullOrWhiteSpace(summary.TicketingMcp))
            {
                sb.AppendLine($"        <div><dt>Ticketing MCP</dt><dd>{HtmlEncode(summary.TicketingMcp!)}</dd></div>");
            }
            if (summary.ApprovedMcpServersForSession.Count > 0)
            {
                sb.AppendLine($"        <div><dt>MCP approved (session)</dt><dd>{HtmlEncode(string.Join(", ", summary.ApprovedMcpServersForSession))}</dd></div>");
            }
            if (summary.PersistedApprovedMcpServers.Count > 0)
            {
                sb.AppendLine($"        <div><dt>MCP approved (persisted)</dt><dd>{HtmlEncode(string.Join(", ", summary.PersistedApprovedMcpServers))}</dd></div>");
            }
            if (summary.UsedSkills.Count > 0)
            {
                sb.AppendLine($"        <div><dt>Skills used</dt><dd>{HtmlEncode(string.Join(", ", summary.UsedSkills))}</dd></div>");
            }
            sb.AppendLine("      </dl>");
            sb.AppendLine("    </div>");
        }

        // ── TOC sidebar ──
        if (prompts.Count > 1)
        {
            sb.AppendLine("    <nav class=\"toc\" aria-label=\"Table of contents\">");
            sb.AppendLine("      <h3>Prompts</h3>");
            sb.AppendLine("      <ol>");
            for (var t = 0; t < prompts.Count; t++)
            {
                var label = TrimForToc(prompts[t].Prompt);
                sb.AppendLine($"        <li><a href=\"#prompt-{t + 1}\">{HtmlEncode(label)}</a></li>");
            }
            sb.AppendLine("      </ol>");
            sb.AppendLine("    </nav>");
        }

        // ── Summary statistics cards ──
        sb.AppendLine("    <div class=\"summary-row\">");
        sb.AppendLine($"      <div class=\"summary-card sc-green\"><div class=\"sc-val\">{safeCount}</div><div class=\"sc-lbl\">Safe (Auto)</div></div>");
        sb.AppendLine($"      <div class=\"summary-card sc-blue\"><div class=\"sc-val\">{approvedCount}</div><div class=\"sc-lbl\">Approved</div></div>");
        sb.AppendLine($"      <div class=\"summary-card sc-red\"><div class=\"sc-val\">{blockedCount}</div><div class=\"sc-lbl\">Blocked</div></div>");
        sb.AppendLine($"      <div class=\"summary-card sc-amber\"><div class=\"sc-val\">{deniedCount}</div><div class=\"sc-lbl\">Denied</div></div>");
        sb.AppendLine("    </div>");

        // ── Timeline of prompt cards ──
        sb.AppendLine("    <div class=\"timeline\">");

        for (var i = 0; i < prompts.Count; i++)
        {
            var prompt = prompts[i];
            var promptTime = prompt.Timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

            sb.AppendLine("      <div class=\"timeline-item\">");
            sb.AppendLine("        <div class=\"timeline-dot\"></div>");
            sb.AppendLine($"        <details class=\"prompt-card\" id=\"prompt-{i + 1}\" open>");
            sb.AppendLine("          <summary class=\"prompt-header\">");
            sb.AppendLine($"            <div class=\"prompt-badge\">{i + 1}</div>");
            sb.AppendLine("            <div class=\"prompt-info\">");
            sb.AppendLine($"              <div class=\"prompt-text\">{HtmlEncode(prompt.Prompt)}</div>");
            sb.AppendLine("              <div class=\"prompt-meta\">");
            sb.AppendLine($"                <span class=\"prompt-meta-item\">&#128337; {HtmlEncode(promptTime)}</span>");
            sb.AppendLine($"                <span class=\"prompt-meta-item\">&#9881; {prompt.Actions.Count} action{(prompt.Actions.Count == 1 ? "" : "s")}</span>");
            sb.AppendLine("              </div>");
            sb.AppendLine("            </div>");
            sb.AppendLine("            <svg class=\"prompt-chevron\" viewBox=\"0 0 20 20\" fill=\"currentColor\"><path fill-rule=\"evenodd\" d=\"M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z\" clip-rule=\"evenodd\" /></svg>");
            sb.AppendLine("          </summary>");
            sb.AppendLine("          <div class=\"prompt-body\">");

            if (prompt.Actions.Count == 0)
            {
                sb.AppendLine("            <div class=\"no-actions\">No actions captured for this prompt.</div>");
            }
            else
            {
                foreach (var action in prompt.Actions)
                {
                    var actionTime = action.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    var isMcp = string.Equals(action.Source, "MCP", StringComparison.OrdinalIgnoreCase);

                    sb.AppendLine("            <div class=\"action-card\">");
                    sb.AppendLine("              <div class=\"action-header\">");
                    sb.AppendLine($"                <span class=\"action-time\">{HtmlEncode(actionTime)}</span>");
                    if (isMcp)
                    {
                        sb.AppendLine($"                <span class=\"mcp-chip\">&#128268; MCP &middot; {HtmlEncode(action.Target)}</span>");
                    }
                    else
                    {
                        sb.AppendLine($"                <span class=\"source-chip\">{HtmlEncode(action.Source)}</span>");
                        if (!string.IsNullOrWhiteSpace(action.Target))
                        {
                            sb.AppendLine($"                <span class=\"action-target\">{HtmlEncode(action.Target)}</span>");
                        }
                    }
                    sb.AppendLine($"                <span class=\"approval-chip approval-{HtmlEncode(action.SafetyApproval)}\">{HtmlEncode(action.SafetyApproval)}</span>");
                    if (action.Success == false)
                    {
                        sb.AppendLine("                <span class=\"approval-chip approval-Failed\">Failed</span>");
                    }
                    sb.AppendLine("              </div>");

                    if (isMcp)
                    {
                        // Tool call (server / tool name without args)
                        sb.AppendLine("              <details class=\"inner-section\" data-md-section=\"Tool call\" open>");
                        sb.AppendLine("                <summary>Tool call</summary>");
                        sb.AppendLine("                <div class=\"inner-content\">");
                        sb.AppendLine($"                  <div class=\"code-wrap\"><button class=\"copy-btn\" onclick=\"copyCode(this)\">Copy</button><pre class=\"output-block\">{HtmlEncode(action.Command)}</pre></div>");
                        sb.AppendLine("                </div>");
                        sb.AppendLine("              </details>");

                        if (!string.IsNullOrWhiteSpace(action.Arguments))
                        {
                            sb.AppendLine("              <details class=\"inner-section\" data-md-section=\"Arguments\">");
                            sb.AppendLine("                <summary>Arguments</summary>");
                            sb.AppendLine("                <div class=\"inner-content\">");
                            sb.AppendLine($"                  <div class=\"code-wrap\"><button class=\"copy-btn\" onclick=\"copyCode(this)\">Copy</button><pre class=\"output-block\">{HtmlEncode(action.Arguments!)}</pre></div>");
                            sb.AppendLine("                </div>");
                            sb.AppendLine("              </details>");
                        }

                        sb.AppendLine("              <details class=\"inner-section\" data-md-section=\"Output\">");
                        sb.AppendLine("                <summary>Output</summary>");
                        sb.AppendLine("                <div class=\"inner-content\">");
                        var outputText = string.IsNullOrWhiteSpace(action.Output) ? "(no output captured)" : action.Output;
                        sb.AppendLine($"                  <div class=\"code-wrap\"><button class=\"copy-btn\" onclick=\"copyCode(this)\">Copy</button><pre class=\"output-block\">{HtmlEncode(outputText)}</pre></div>");
                        sb.AppendLine("                </div>");
                        sb.AppendLine("              </details>");
                    }
                    else
                    {
                        // PowerShell command + output
                        sb.AppendLine("              <details class=\"inner-section\" data-md-section=\"Command\" open>");
                        sb.AppendLine("                <summary>Command</summary>");
                        sb.AppendLine("                <div class=\"inner-content\">");
                        sb.AppendLine($"                  <div class=\"code-wrap\"><button class=\"copy-btn\" onclick=\"copyCode(this)\">Copy</button><pre class=\"code-block\">{RenderCommandHtmlWithLineNumbers(action.Command)}</pre></div>");
                        sb.AppendLine("                </div>");
                        sb.AppendLine("              </details>");

                        sb.AppendLine("              <details class=\"inner-section\" data-md-section=\"Output\">");
                        sb.AppendLine("                <summary>Output</summary>");
                        sb.AppendLine("                <div class=\"inner-content\">");
                        sb.AppendLine($"                  <div class=\"code-wrap\"><button class=\"copy-btn\" onclick=\"copyCode(this)\">Copy</button><pre class=\"output-block\">{HtmlEncode(action.Output)}</pre></div>");
                        sb.AppendLine("                </div>");
                        sb.AppendLine("              </details>");
                    }
                    sb.AppendLine("            </div>");
                }
            }

            // Agent reply chat bubble (markdown rendered)
            sb.AppendLine("            <div class=\"reply-bubble\">");
            sb.AppendLine("              <div class=\"reply-header\">");
            sb.AppendLine("                <div class=\"reply-avatar\">");
            sb.AppendLine("                  <svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"#fff\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><path d=\"M12 2a4 4 0 0 1 4 4v2a4 4 0 0 1-8 0V6a4 4 0 0 1 4-4z\" /><path d=\"M20 21v-2a4 4 0 0 0-3-3.87\" /><path d=\"M4 21v-2a4 4 0 0 1 3-3.87\" /><circle cx=\"12\" cy=\"17\" r=\"4\" fill=\"rgba(255,255,255,0.15)\" stroke=\"#fff\" /></svg>");
            sb.AppendLine("                </div>");
            sb.AppendLine("                <span class=\"reply-label\">Agent Reply</span>");
            sb.AppendLine("              </div>");
            if (string.IsNullOrWhiteSpace(prompt.AgentReply))
            {
                sb.AppendLine("              <div class=\"muted\">No assistant reply captured for this prompt.</div>");
            }
            else
            {
                // Stash raw markdown in a script tag to avoid HTML escaping issues, render via marked+DOMPurify on load.
                sb.AppendLine($"              <script type=\"text/markdown\" class=\"reply-md-source\" data-target=\"reply-md-{i + 1}\">{EscapeForScriptBlock(prompt.AgentReply)}</script>");
                sb.AppendLine($"              <div class=\"reply-markdown\" id=\"reply-md-{i + 1}\" data-md-render>{HtmlEncode(prompt.AgentReply)}</div>");
                sb.AppendLine("              <div class=\"reply-actions\">");
                sb.AppendLine($"                <button class=\"reply-action-btn\" type=\"button\" data-copy-md=\"reply-md-{i + 1}\">&#128203; Copy markdown</button>");
                sb.AppendLine($"                <button class=\"reply-action-btn\" type=\"button\" data-copy-html=\"reply-md-{i + 1}\">&#10063; Copy rendered</button>");
                sb.AppendLine("              </div>");
            }

            // Per-prompt status bar (model + tokens + tools)
            if (prompt.StatusBar != null)
            {
                sb.AppendLine("            <div class=\"statusbar\">");
                AppendStatusBarChips(sb, prompt.StatusBar);
                sb.AppendLine("            </div>");
            }

            sb.AppendLine("            </div>");

            sb.AppendLine("          </div>");
            sb.AppendLine("        </details>");
            sb.AppendLine("      </div>");
        }

        sb.AppendLine("    </div>");

        // ── Footer ──
        sb.AppendLine($"    <div class=\"footer\">Generated by <strong>TroubleScout</strong> &middot; {HtmlEncode(generatedAt)}</div>");

        sb.AppendLine("  </div>");

        // ── Embedded marked.js (markdown parser) ──
        sb.AppendLine("  <script>");
        sb.Append(LoadEmbeddedScript("Assets.report.marked.min.js"));
        sb.AppendLine();
        sb.AppendLine("  </script>");

        // ── Embedded DOMPurify (HTML sanitiser) ──
        sb.AppendLine("  <script>");
        sb.Append(LoadEmbeddedScript("Assets.report.purify.min.js"));
        sb.AppendLine();
        sb.AppendLine("  </script>");

        // ── Main report script ──
        sb.AppendLine("  <script>");
        sb.AppendLine("    function copyCode(btn) {");
        sb.AppendLine("      var pre = btn.parentElement.querySelector('pre');");
        sb.AppendLine("      if (!pre) return;");
        sb.AppendLine("      copyText(pre.textContent || pre.innerText, btn);");
        sb.AppendLine("    }");
        sb.AppendLine("    function copyText(text, btn) {");
        sb.AppendLine("      if (navigator.clipboard && navigator.clipboard.writeText) {");
        sb.AppendLine("        navigator.clipboard.writeText(text).then(function() { showCopied(btn); }, function() { fallbackCopy(text, btn); });");
        sb.AppendLine("      } else { fallbackCopy(text, btn); }");
        sb.AppendLine("    }");
        sb.AppendLine("    function fallbackCopy(text, btn) {");
        sb.AppendLine("      var ta = document.createElement('textarea');");
        sb.AppendLine("      ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';");
        sb.AppendLine("      document.body.appendChild(ta); ta.select();");
        sb.AppendLine("      try { document.execCommand('copy'); showCopied(btn); } catch(e) {}");
        sb.AppendLine("      document.body.removeChild(ta);");
        sb.AppendLine("    }");
        sb.AppendLine("    function showCopied(btn) {");
        sb.AppendLine("      var orig = btn.textContent;");
        sb.AppendLine("      btn.textContent = '\\u2713 Copied'; btn.classList.add('copied');");
        sb.AppendLine("      setTimeout(function() { btn.textContent = orig; btn.classList.remove('copied'); }, 1500);");
        sb.AppendLine("    }");
        sb.AppendLine("    function getRawMarkdownFor(targetId) {");
        sb.AppendLine("      var src = document.querySelector('script.reply-md-source[data-target=\"' + targetId + '\"]');");
        sb.AppendLine("      return src ? src.textContent : '';");
        sb.AppendLine("    }");
        sb.AppendLine("    function renderAllMarkdown() {");
        sb.AppendLine("      if (typeof marked === 'undefined' || typeof DOMPurify === 'undefined') return;");
        sb.AppendLine("      try { marked.setOptions({ gfm: true, breaks: false }); } catch(e) {}");
        sb.AppendLine("      document.querySelectorAll('[data-md-render]').forEach(function(el) {");
        sb.AppendLine("        var raw = getRawMarkdownFor(el.id);");
        sb.AppendLine("        if (!raw) return;");
        sb.AppendLine("        try {");
        sb.AppendLine("          var html = marked.parse(raw);");
        sb.AppendLine("          el.innerHTML = DOMPurify.sanitize(html, { USE_PROFILES: { html: true } });");
        sb.AppendLine("        } catch(e) { /* leave plain text fallback in place */ }");
        sb.AppendLine("      });");
        sb.AppendLine("    }");
        sb.AppendLine("    function buildSessionMarkdown() {");
        sb.AppendLine("      var lines = ['# TroubleScout Session Report', ''];");
        sb.AppendLine("      var prompts = document.querySelectorAll('details.prompt-card');");
        sb.AppendLine("      prompts.forEach(function(card, idx) {");
        sb.AppendLine("        var promptText = card.querySelector('.prompt-text');");
        sb.AppendLine("        lines.push('## Prompt ' + (idx + 1));");
        sb.AppendLine("        lines.push('');");
        sb.AppendLine("        if (promptText) lines.push('> ' + promptText.textContent.trim());");
        sb.AppendLine("        lines.push('');");
        sb.AppendLine("        var actions = card.querySelectorAll('.action-card');");
        sb.AppendLine("        if (actions.length) {");
        sb.AppendLine("          lines.push('### Actions');");
        sb.AppendLine("          lines.push('');");
        sb.AppendLine("          actions.forEach(function(a) {");
        sb.AppendLine("            var header = a.querySelector('.action-header');");
        sb.AppendLine("            if (header) lines.push('**' + header.textContent.trim().replace(/\\s+/g,' ') + '**');");
        sb.AppendLine("            lines.push('');");
        sb.AppendLine("            a.querySelectorAll('details.inner-section[data-md-section]').forEach(function(sec) {");
        sb.AppendLine("              var label = sec.getAttribute('data-md-section') || '';");
        sb.AppendLine("              var pre = sec.querySelector('pre');");
        sb.AppendLine("              if (!pre) return;");
        sb.AppendLine("              if (label) lines.push('_' + label + ':_');");
        sb.AppendLine("              lines.push('```');");
        sb.AppendLine("              lines.push(pre.textContent.replace(/\\s+$/,''));");
        sb.AppendLine("              lines.push('```');");
        sb.AppendLine("              lines.push('');");
        sb.AppendLine("            });");
        sb.AppendLine("          });");
        sb.AppendLine("        }");
        sb.AppendLine("        var src = card.querySelector('script.reply-md-source');");
        sb.AppendLine("        if (src) {");
        sb.AppendLine("          lines.push('### Reply');");
        sb.AppendLine("          lines.push('');");
        sb.AppendLine("          lines.push(src.textContent);");
        sb.AppendLine("          lines.push('');");
        sb.AppendLine("        }");
        sb.AppendLine("      });");
        sb.AppendLine("      return lines.join('\\n');");
        sb.AppendLine("    }");
        sb.AppendLine("    function exportMarkdown() {");
        sb.AppendLine("      var md = buildSessionMarkdown();");
        sb.AppendLine("      var blob = new Blob([md], { type: 'text/markdown;charset=utf-8' });");
        sb.AppendLine("      var url = URL.createObjectURL(blob);");
        sb.AppendLine("      var a = document.createElement('a');");
        sb.AppendLine("      a.href = url;");
        sb.AppendLine("      a.download = 'troublescout-session-' + new Date().toISOString().slice(0,19).replace(/[:T]/g,'-') + '.md';");
        sb.AppendLine("      document.body.appendChild(a); a.click(); document.body.removeChild(a);");
        sb.AppendLine("      setTimeout(function() { URL.revokeObjectURL(url); }, 1000);");
        sb.AppendLine("    }");
        sb.AppendLine("    function applyTheme(theme) {");
        sb.AppendLine("      document.documentElement.setAttribute('data-theme', theme);");
        sb.AppendLine("      try { localStorage.setItem('troublescout-report-theme', theme); } catch(e) {}");
        sb.AppendLine("    }");
        sb.AppendLine("    function initReport() {");
        sb.AppendLine("      var stored = null;");
        sb.AppendLine("      try { stored = localStorage.getItem('troublescout-report-theme'); } catch(e) {}");
        sb.AppendLine("      if (stored === 'light' || stored === 'dark') applyTheme(stored);");
        sb.AppendLine("      else applyTheme('dark');");
        sb.AppendLine("      var tt = document.getElementById('theme-toggle-btn');");
        sb.AppendLine("      if (tt) tt.addEventListener('click', function() {");
        sb.AppendLine("        var cur = document.documentElement.getAttribute('data-theme');");
        sb.AppendLine("        applyTheme(cur === 'light' ? 'dark' : 'light');");
        sb.AppendLine("      });");
        sb.AppendLine("      var ex = document.getElementById('export-md-btn');");
        sb.AppendLine("      if (ex) ex.addEventListener('click', exportMarkdown);");
        sb.AppendLine("      document.querySelectorAll('[data-copy-md]').forEach(function(btn) {");
        sb.AppendLine("        btn.addEventListener('click', function() { copyText(getRawMarkdownFor(btn.getAttribute('data-copy-md')), btn); });");
        sb.AppendLine("      });");
        sb.AppendLine("      document.querySelectorAll('[data-copy-html]').forEach(function(btn) {");
        sb.AppendLine("        btn.addEventListener('click', function() {");
        sb.AppendLine("          var el = document.getElementById(btn.getAttribute('data-copy-html'));");
        sb.AppendLine("          copyText(el ? (el.innerText || el.textContent || '') : '', btn);");
        sb.AppendLine("        });");
        sb.AppendLine("      });");
        sb.AppendLine("      renderAllMarkdown();");
        sb.AppendLine("    }");
        sb.AppendLine("    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initReport); else initReport();");
        sb.AppendLine("  </script>");

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    internal static string HtmlEncode(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static readonly Regex CommandTokenRegex = new(
        "(?<string>'[^'\\n\\r]*'|\"[^\"\\n\\r]*\")" +
        "|(?<variable>\\$[A-Za-z_][\\w:]*)" +
        "|(?<param>-[A-Za-z][\\w-]*)" +
        "|(?<cmdlet>\\b[A-Za-z]+-[A-Za-z][A-Za-z0-9]*\\b)" +
        "|(?<number>\\b\\d+(?:\\.\\d+)?\\b)" +
        "|(?<op>(?:-eq|-ne|-gt|-ge|-lt|-le|-and|-or|-not)\\b|[|;])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string RenderCommandHtml(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(command.Length + 32);
        var lastIndex = 0;

        foreach (Match match in CommandTokenRegex.Matches(command))
        {
            if (!match.Success)
            {
                continue;
            }

            if (match.Index > lastIndex)
            {
                builder.Append(HtmlEncode(command.Substring(lastIndex, match.Index - lastIndex)));
            }

            var cssClass = match.Groups["string"].Success ? "tok-string" :
                           match.Groups["variable"].Success ? "tok-variable" :
                           match.Groups["param"].Success ? "tok-param" :
                           match.Groups["cmdlet"].Success ? "tok-cmdlet" :
                           match.Groups["number"].Success ? "tok-number" :
                           match.Groups["op"].Success ? "tok-op" : string.Empty;

            var tokenText = HtmlEncode(match.Value);
            if (string.IsNullOrEmpty(cssClass))
            {
                builder.Append(tokenText);
            }
            else
            {
                builder.Append("<span class=\"").Append(cssClass).Append("\">").Append(tokenText).Append("</span>");
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < command.Length)
        {
            builder.Append(HtmlEncode(command.Substring(lastIndex)));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders a command with syntax highlighting, splitting multi-line commands into
    /// separate code-line spans so CSS counters produce per-line numbers.
    /// </summary>
    internal static string RenderCommandHtmlWithLineNumbers(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return "<span class=\"code-line\"></span>";
        }

        var highlighted = RenderCommandHtml(command);
        // Split the already-highlighted HTML on literal newlines to produce one code-line per source line
        var lines = highlighted.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append("<span class=\"code-line\">").Append(line).AppendLine("</span>");
        }
        return sb.ToString();
    }

    private static void AppendStatusBarChips(StringBuilder sb, TroubleScout.UI.StatusBarInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Model))
        {
            sb.AppendLine($"              <span class=\"sb-chip sb-model\" title=\"Model\">&#129504; <strong>{HtmlEncode(info.Model!)}</strong></span>");
        }

        if (!string.IsNullOrWhiteSpace(info.Provider))
        {
            sb.AppendLine($"              <span class=\"sb-chip\" title=\"Provider\">{HtmlEncode(info.Provider!)}</span>");
        }

        if (!string.IsNullOrWhiteSpace(info.ReasoningEffort))
        {
            sb.AppendLine($"              <span class=\"sb-chip\" title=\"Reasoning effort\">&#128173; {HtmlEncode(info.ReasoningEffort!)}</span>");
        }

        if (info.InputTokens.HasValue || info.OutputTokens.HasValue)
        {
            var tokensText = $"{FormatTokenCount(info.InputTokens)} in / {FormatTokenCount(info.OutputTokens)} out";
            sb.AppendLine($"              <span class=\"sb-chip sb-tokens\" title=\"Tokens this turn\">&#128203; {HtmlEncode(tokensText)}</span>");
        }

        if (info.ToolInvocations > 0)
        {
            sb.AppendLine($"              <span class=\"sb-chip sb-tools\" title=\"Tool invocations\">&#128295; Tools: <strong>{info.ToolInvocations}</strong></span>");
        }

        if (info.SessionInputTokens.HasValue || info.SessionOutputTokens.HasValue)
        {
            var sessionText = $"Session: {FormatLongTokenCount(info.SessionInputTokens)} in / {FormatLongTokenCount(info.SessionOutputTokens)} out";
            sb.AppendLine($"              <span class=\"sb-chip\" title=\"Session totals\">{HtmlEncode(sessionText)}</span>");
        }

        if (!string.IsNullOrWhiteSpace(info.SessionCostEstimate))
        {
            sb.AppendLine($"              <span class=\"sb-chip\" title=\"Premium request estimate\">&#11088; {HtmlEncode(info.SessionCostEstimate!)}</span>");
        }
    }

    private static string FormatTokenCount(int? value)
    {
        if (!value.HasValue)
        {
            return "?";
        }

        return FormatTokenCountCore(value.Value);
    }

    private static string FormatLongTokenCount(long? value)
    {
        if (!value.HasValue)
        {
            return "?";
        }

        return FormatTokenCountCore(value.Value);
    }

    private static string FormatTokenCountCore(long v)
    {
        if (v >= 1000)
        {
            return (v / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        }

        return v.ToString(CultureInfo.InvariantCulture);
    }

    private static string TrimForToc(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        var firstLine = text.Split('\n', 2)[0].Trim();
        const int max = 64;
        return firstLine.Length <= max ? firstLine : firstLine.Substring(0, max - 1) + "\u2026";
    }

    private static string EscapeForScriptBlock(string raw)
    {
        // Inside a non-executable <script type="text/markdown"> block we must only escape "</" sequences
        // (which would otherwise terminate the script element). Everything else passes through verbatim
        // so the markdown survives unchanged.
        return raw.Replace("</", "<\\/");
    }

    private static string LoadEmbeddedScript(string resourceSuffix)
    {
        try
        {
            var assembly = typeof(ReportHtmlBuilder).Assembly;
            var fullName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));

            if (fullName == null)
            {
                return $"/* embedded resource '{resourceSuffix}' not found */";
            }

            using var stream = assembly.GetManifestResourceStream(fullName);
            if (stream == null)
            {
                return $"/* embedded resource '{resourceSuffix}' could not be opened */";
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return $"/* failed to load embedded resource '{resourceSuffix}': {ex.Message} */";
        }
    }

}
