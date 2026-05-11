using TroubleScout.Tools;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal sealed class PendingCommandApprovalProcessor(
    DiagnosticTools diagnosticTools,
    Func<string, int> recordPrompt,
    Func<string, int, CancellationToken, bool, bool, Task<bool>> sendMessage)
{
    internal async Task<bool> ProcessAsync(CancellationToken cancellationToken)
    {
        var pending = diagnosticTools.PendingCommands;
        if (pending.Count == 0)
        {
            return false;
        }

        var commands = pending.Select(p => (p.Command, p.Reason)).ToList();
        var executedSummaries = new List<string>();

        if (commands.Count == 1)
        {
            var cmd = commands[0];
            var approval = ConsoleUI.PromptCommandApproval(cmd.Command, cmd.Reason, pending[0].Intent);
            if (approval == ApprovalResult.Approved)
            {
                ConsoleUI.ShowInfo($"Executing: {cmd.Command}");
                var result = await diagnosticTools.ExecuteApprovedCommandAsync(pending[0]);
                ConsoleUI.ShowSuccess("Command executed");
                executedSummaries.Add($"Command: {cmd.Command}{Environment.NewLine}Result:{Environment.NewLine}{result}");
            }
            else
            {
                ConsoleUI.ShowWarning("Command skipped by user");
                diagnosticTools.LogDeniedCommand(pending[0]);
                diagnosticTools.ClearPendingCommands();
                return false;
            }
        }
        else
        {
            var approved = ConsoleUI.PromptBatchApproval(commands);

            var pendingSnapshot = pending.ToList();
            foreach (var index in approved)
            {
                var cmd = pendingSnapshot[index - 1];
                ConsoleUI.ShowInfo($"Executing: {cmd.Command}");
                var result = await diagnosticTools.ExecuteApprovedCommandAsync(cmd);
                ConsoleUI.ShowSuccess("Command executed");
                executedSummaries.Add($"Command: {cmd.Command}{Environment.NewLine}Result:{Environment.NewLine}{result}");
            }

            var approvedSet = new HashSet<int>(approved);
            for (var i = 0; i < pendingSnapshot.Count; i++)
            {
                if (!approvedSet.Contains(i + 1))
                {
                    diagnosticTools.LogDeniedCommand(pendingSnapshot[i]);
                }
            }

            diagnosticTools.ClearPendingCommands();
        }

        if (executedSummaries.Count == 0)
        {
            return false;
        }

        var promptIndex = recordPrompt("TroubleScout action: Analyze approved command results.");
        var followUpPrompt = SessionPromptFlow.BuildApprovedCommandFollowUpPrompt(string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            executedSummaries));

        return await sendMessage(
            followUpPrompt,
            promptIndex,
            cancellationToken,
            true,
            true);
    }
}
