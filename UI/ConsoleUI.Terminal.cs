using Spectre.Console;

namespace TroubleScout.UI;

public static partial class ConsoleUI
{
    public static void BeginAppLifetime(string title = "TroubleScout")
    {
        lock (_terminalStateLock)
        {
            if (!_capturedOriginalConsoleTitle)
            {
                try
                {
                    _originalConsoleTitle = OperatingSystem.IsWindows()
                        ? Console.Title
                        : null;
                }
                catch
                {
                    _originalConsoleTitle = null;
                }

                _capturedOriginalConsoleTitle = true;
            }
        }

        SetTerminalTitle(title);
    }

    public static void EndAppLifetime()
    {
        ClearWindowsTerminalProgress();

        lock (_terminalStateLock)
        {
            if (!_capturedOriginalConsoleTitle)
            {
                return;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(_originalConsoleTitle))
                {
                    SetTerminalTitle(_originalConsoleTitle);
                }
            }
            finally
            {
                _capturedOriginalConsoleTitle = false;
                _originalConsoleTitle = null;
            }
        }
    }

    internal static string BuildTerminalTitleSequence(string title)
        => $"\u001b]0;{title}\u0007\u001b]2;{title}\u0007";

    internal static string BuildWindowsTerminalProgressSequence(TerminalProgressState state, int progress = 0)
        => $"\u001b]9;4;{(int)state};{Math.Clamp(progress, 0, 100)}\u0007";

    public static void SetTerminalTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Console.Title = title;
            }
        }
        catch
        {
            // Ignore when no console title API is available.
        }

        // Only emit OSC title sequences inside Windows Terminal and when stdout
        // is not redirected. Other terminals or pipelines should not see the bytes.
        if (IsOutputRedirectedResolver() || !IsWindowsTerminalSessionResolver())
        {
            return;
        }

        try
        {
            Console.Write(BuildTerminalTitleSequence(title));
        }
        catch
        {
            // Ignore when escape sequences cannot be written.
        }
    }

    public static void SetWindowsTerminalProgress(TerminalProgressState state, int progress = 0)
    {
        if (!IsWindowsTerminalSessionResolver() || IsOutputRedirectedResolver())
        {
            return;
        }

        try
        {
            Console.Write(BuildWindowsTerminalProgressSequence(state, progress));
        }
        catch
        {
            // Ignore when the active terminal does not accept progress sequences.
        }
    }

    public static void ClearWindowsTerminalProgress()
        => SetWindowsTerminalProgress(TerminalProgressState.Hidden);

    /// <summary>
    /// Context manager for the thinking indicator
    /// </summary>
    private class ThinkingContext : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _spinnerTask;

        public ThinkingContext(Status status, string message)
        {
            _spinnerTask = Task.Run(async () =>
            {
                try
                {
                    await status.StartAsync(message, async ctx =>
                    {
                        while (!_cts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(100, _cts.Token);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    // Expected when disposed
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _spinnerTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Ignore timeout
            }
            _cts.Dispose();
        }
    }
}
