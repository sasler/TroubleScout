using TroubleScout.UI;

namespace TroubleScout.Services;

internal static class InteractiveSessionLoop
{
    internal static async Task RunAsync(
        ExecutionMode executionMode,
        IReadOnlyList<string> slashCommands,
        Func<SlashCommandDispatcher> createSlashCommandDispatcher,
        Func<string, int> recordPrompt,
        Func<string, int, CancellationToken, Task<bool>> sendMessage)
    {
        ConsoleUI.SetExecutionMode(executionMode);
        var slashCommandDispatcher = createSlashCommandDispatcher();

        while (true)
        {
            var input = ConsoleUI.GetUserInput(slashCommands).Trim();

            if (!string.IsNullOrEmpty(input))
            {
                ConsoleUI.AddPromptHistory(input);
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            var slashCommandResult = await slashCommandDispatcher.DispatchAsync(input);
            if (slashCommandResult.Handled)
            {
                if (slashCommandResult.ExitRequested)
                {
                    break;
                }

                continue;
            }

            var promptIndex = recordPrompt(input);
            await RunCancelableAiOperationAsync(token => sendMessage(input, promptIndex, token));
        }
    }

    internal static async Task<T> RunCancelableAiOperationAsync<T>(Func<CancellationToken, Task<T>> operation)
    {
        using var escCts = new CancellationTokenSource();
        var escTask = Task.Run(async () =>
        {
            try
            {
                while (!escCts.Token.IsCancellationRequested)
                {
                    if (Console.KeyAvailable && !LiveThinkingIndicator.IsApprovalInProgress)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Escape)
                        {
                            escCts.Cancel();
                            break;
                        }
                    }

                    await Task.Delay(50, escCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the AI finishes before ESC is pressed.
            }
        }, CancellationToken.None);

        try
        {
            return await operation(escCts.Token);
        }
        finally
        {
            escCts.Cancel();
            try { await escTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { /* ignore */ }
        }
    }
}
