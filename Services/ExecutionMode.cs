namespace TroubleScout.Services;

public enum ExecutionMode
{
    Safe,
    Yolo
}

public static class ExecutionModeParser
{
    public static bool TryParse(string? input, out ExecutionMode mode)
    {
        mode = ExecutionMode.Safe;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        switch (input.Trim().ToLowerInvariant())
        {
            case "safe":
                mode = ExecutionMode.Safe;
                return true;
            case "yolo":
                mode = ExecutionMode.Yolo;
                return true;
            default:
                return false;
        }
    }

    public static string ToCliValue(this ExecutionMode mode)
    {
        return mode switch
        {
            ExecutionMode.Safe => "safe",
            ExecutionMode.Yolo => "yolo",
            _ => "safe"
        };
    }
}