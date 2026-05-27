namespace TroubleScout.Services;

public enum ExecutionMode
{
    Strict,
    Auto
}

public static class ExecutionModeParser
{
    public static bool TryParse(string? input, out ExecutionMode mode)
    {
        mode = ExecutionMode.Strict;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        switch (input.Trim().ToLowerInvariant())
        {
            case "strict":
                mode = ExecutionMode.Strict;
                return true;
            case "auto":
                mode = ExecutionMode.Auto;
                return true;
            default:
                return false;
        }
    }

    public static string ToCliValue(this ExecutionMode mode)
    {
        return mode switch
        {
            ExecutionMode.Strict => "strict",
            ExecutionMode.Auto => "auto",
            _ => "strict"
        };
    }
}
