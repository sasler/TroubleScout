namespace TroubleScout.UI;

/// <summary>
/// A live thinking indicator that shows animated status and can be updated or stopped
/// </summary>
public class LiveThinkingIndicator : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private string _currentStatus = "Thinking";
    private bool _isRunning;
    private bool _isDisposed;
    private bool _hasStartedResponse;
    private readonly object _lock = new();
    private Task? _spinnerTask;
    private static readonly string[] SpinnerFrames =
    [
        "/ [ DNS? ]",
        "- [ FW?  ]",
        "\\ [ RAM? ]",
        "| [ HDD? ]",
        "/ [ CPU? ]",
        "- [ NIC? ]",
        "\\ [ CERT?]",
        "| [ USER?]"
    ];
    private int _spinnerIndex;
    private TerminalProgressState? _lastProgressState;
    private int? _lastProgressValue;
    private readonly System.Diagnostics.Stopwatch _totalElapsed = new();
    private readonly System.Diagnostics.Stopwatch _phaseElapsed = new();

    private static volatile bool _approvalInProgress;

    /// <summary>Whether an approval dialog is currently suppressing spinner output.</summary>
    public static bool IsApprovalInProgress => _approvalInProgress;

    /// <summary>Pause spinner output while an approval dialog is visible.</summary>
    public static void PauseForApproval() => _approvalInProgress = true;

    /// <summary>Resume spinner output after an approval dialog completes.</summary>
    public static void ResumeAfterApproval() => _approvalInProgress = false;

    /// <summary>Total elapsed time since the indicator started.</summary>
    public TimeSpan Elapsed => _totalElapsed.Elapsed;

    /// <summary>Elapsed time since the last phase change.</summary>
    public TimeSpan PhaseElapsed => _phaseElapsed.Elapsed;

    public void Start()
    {
        lock (_lock)
        {
            if (_isRunning) return;
            _isRunning = true;
            _isDisposed = false;
            _hasStartedResponse = false;
            _lastProgressState = null;
            _lastProgressValue = null;
            _totalElapsed.Restart();
            _phaseElapsed.Restart();
            UpdateTerminalProgress(TerminalProgressState.Indeterminate);
        }

        _spinnerTask = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    lock (_lock)
                    {
                        if (_hasStartedResponse) break;

                        if (!_approvalInProgress)
                        {
                            WriteSpinnerFrame();
                        }
                    }
                    await Task.Delay(200, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        });
    }

    private void WriteSpinnerFrame()
    {
        var phaseSec = (int)_phaseElapsed.Elapsed.TotalSeconds;
        var totalSec = (int)_totalElapsed.Elapsed.TotalSeconds;
        var elapsed = FormatElapsed(totalSec);
        var animationFrame = SpinnerFrames[_spinnerIndex];

        string statusColor;
        string status;
        string hint;

        if (phaseSec >= 60)
        {
            statusColor = "\u001b[33m";
            status = $"{animationFrame} Still waiting ({elapsed})";
            hint = "operation may be stalled \u2014 ESC to cancel";
            UpdateTerminalProgress(TerminalProgressState.Warning, 100);
        }
        else if (phaseSec >= 30)
        {
            statusColor = "\u001b[33m";
            status = $"{animationFrame} Still working ({elapsed})";
            hint = "this is taking longer than usual \u2014 ESC to cancel";
            UpdateTerminalProgress(TerminalProgressState.Warning, 100);
        }
        else
        {
            statusColor = "\u001b[36m";
            status = $"{animationFrame} {_currentStatus}";
            if (totalSec >= 3)
            {
                status += $" ({elapsed})";
            }

            hint = "ESC to cancel";
            UpdateTerminalProgress(TerminalProgressState.Indeterminate);
        }

        Console.Write($"\r\x1b[K{statusColor}{status}\u001b[0m  \u001b[90m{hint}\u001b[0m");
        _spinnerIndex = (_spinnerIndex + 1) % SpinnerFrames.Length;
    }

    private void UpdateTerminalProgress(TerminalProgressState state, int progress = 0)
    {
        if (_cts.IsCancellationRequested || _isDisposed)
        {
            return;
        }

        progress = Math.Clamp(progress, 0, 100);
        if (_lastProgressState == state && _lastProgressValue == progress)
        {
            return;
        }

        _lastProgressState = state;
        _lastProgressValue = progress;
        ConsoleUI.SetWindowsTerminalProgress(state, progress);
    }

    private void ClearTerminalProgress()
    {
        _lastProgressState = TerminalProgressState.Hidden;
        _lastProgressValue = 0;
        ConsoleUI.ClearWindowsTerminalProgress();
    }

    internal static string FormatElapsed(int totalSeconds)
    {
        if (totalSeconds < 60)
        {
            return $"{totalSeconds}s";
        }

        var min = totalSeconds / 60;
        var sec = totalSeconds % 60;
        return sec > 0 ? $"{min}m {sec}s" : $"{min}m";
    }

    public void UpdateStatus(string status)
    {
        lock (_lock)
        {
            _currentStatus = status;
            _phaseElapsed.Restart();
        }
    }

    public void ShowToolExecution(string toolName)
    {
        lock (_lock)
        {
            _currentStatus = $"Running {toolName}";
            _phaseElapsed.Restart();
        }
    }

    public void StopForResponse()
    {
        lock (_lock)
        {
            if (_hasStartedResponse) return;
            _hasStartedResponse = true;
            
            // Clear the status line
            Console.Write("\r\x1b[K");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        _cts.Cancel();

        try
        {
            _spinnerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore
        }

        lock (_lock)
        {
            _totalElapsed.Stop();
            _phaseElapsed.Stop();

            if (!_hasStartedResponse)
            {
                // Clear the status line if we haven't started a response
                Console.Write("\r\x1b[K");
            }

            ClearTerminalProgress();
        }

        _cts.Dispose();
    }
}
