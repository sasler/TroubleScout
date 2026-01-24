namespace TroubleScout.Tests.Fixtures;

/// <summary>
/// Common test data and helpers for TroubleScout tests
/// </summary>
public static class TestHelpers
{
    /// <summary>
    /// Test timeout configurations
    /// </summary>
    public static class Timeouts
    {
        public static readonly TimeSpan Fast = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan Normal = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan Slow = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Sample PowerShell commands for testing
    /// </summary>
    public static class SampleCommands
    {
        // Safe read-only commands (should auto-execute)
        public static readonly string[] SafeCommands = 
        [
            "Get-Service",
            "Get-Process",
            "Get-EventLog -LogName System -Newest 10",
            "Get-Disk | Format-List",
            "Get-Volume | Select-Object DriveLetter,FileSystem",
        ];

        // Commands requiring approval (modification commands)
        public static readonly string[] UnsafeCommands = 
        [
            "Set-Service -Name wuauserv -StartupType Manual",
            "Restart-Service -Name wuauserv",
            "Stop-Process -Name notepad",
            "Remove-Item -Path C:\\temp\\test.txt",
            "Start-Service -Name spooler",
        ];

        // Blocked commands (credential/secret access)
        public static readonly string[] BlockedCommands = 
        [
            "Get-Credential",
            "Get-Secret -Name MySecret",
            "Get-Credential -UserName admin",
        ];

        // Edge cases and potential injection attempts
        public static readonly string[] EdgeCases = 
        [
            "",
            "   ",
            "Get-Process; Remove-Item C:\\test.txt",
            "Get-Service `; Remove-Item C:\\test.txt",
            "Get-Process | ForEach-Object { $_.Kill() }",
        ];

        // Multi-line read-only script
        public static readonly string ReadOnlyScript = @"
$services = Get-Service
$processes = Get-Process
Write-Output ""Services: $($services.Count)""
Write-Output ""Processes: $($processes.Count)""
";

        // Multi-line script with unsafe commands
        public static readonly string MixedScript = @"
$services = Get-Service
Stop-Service -Name wuauserv
Write-Output ""Service stopped""
";

        // Format-Volume vs Format-List (regression test)
        public static readonly string FormatVolumeCommand = "Get-Disk | Format-Volume";
        public static readonly string FormatListCommand = "Get-Disk | Format-List";
    }

    /// <summary>
    /// Sample PowerShell outputs for mocking
    /// </summary>
    public static class SampleOutputs
    {
        public static readonly string ServiceOutput = @"
Status   Name               DisplayName
------   ----               -----------
Running  wuauserv           Windows Update
Stopped  spooler            Print Spooler
";

        public static readonly string ProcessOutput = @"
Handles  NPM(K)    PM(K)      WS(K)     CPU(s)     Id  SI ProcessName
-------  ------    -----      -----     ------     --  -- -----------
    123       8     1234       5678       0.50   4321   1 notepad
    456      12     2345       6789       1.25   8765   1 pwsh
";

        public static readonly string EventLogOutput = @"
   Index Time          EntryType   Source                 InstanceID Message
   ----- ----          ---------   ------                 ---------- -------
    1234 Jan 24 10:00  Information Service Control Manager 7036      Windows Update service entered the running state.
";

        public static readonly string ErrorOutput = "Get-Service : Cannot find any service with service name 'nonexistent'.";
    }

    /// <summary>
    /// Create a temporary directory for test file operations
    /// </summary>
    public static string CreateTempDirectory()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"TroubleScout.Tests.{Guid.NewGuid()}");
        Directory.CreateDirectory(tempPath);
        return tempPath;
    }

    /// <summary>
    /// Clean up temporary directory
    /// </summary>
    public static void CleanupTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
