namespace TroubleScout.UI;

public static partial class ConsoleUI
{
    /// <summary>
    /// Display reasoning/thinking text in a visually muted dark color
    /// </summary>
    public static void WriteReasoningText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        WithLiveOutputLock(() =>
        {
            if (IsOutputRedirectedResolver())
            {
                Console.Write(text);
            }
            else
            {
                // ANSI dark grey (color 238 on 256-color palette) — clearly dimmer than normal output
                Console.Write($"\x1b[38;5;238m{text}\x1b[0m");
            }
        });
    }

    /// <summary>
    /// Start a reasoning/thinking block with a muted prefix label
    /// </summary>
    public static void StartReasoningBlock()
    {
        WithLiveOutputLock(() =>
        {
            EnsureLineBreak();
            if (!IsOutputRedirectedResolver())
                Console.Write("\x1b[38;5;238m\U0001f4ad \x1b[0m");  // dark grey thinking emoji prefix
        });
    }

    /// <summary>
    /// End a reasoning/thinking block
    /// </summary>
    public static void EndReasoningBlock()
    {
        WithLiveOutputLock(() =>
        {
            EnsureLineBreak();
            Console.WriteLine();
        });
    }
}
