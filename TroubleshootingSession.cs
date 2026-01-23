using GitHub.Copilot.SDK;
using TroubleScout.Services;
using TroubleScout.Tools;
using TroubleScout.UI;

namespace TroubleScout;

/// <summary>
/// Manages the Copilot-powered troubleshooting session
/// </summary>
public class TroubleshootingSession : IAsyncDisposable
{
    private readonly string _targetServer;
    private readonly PowerShellExecutor _executor;
    private readonly DiagnosticTools _diagnosticTools;
    private CopilotClient? _copilotClient;
    private CopilotSession? _copilotSession;
    private bool _isInitialized;
    private string? _selectedModel;
    private string? _copilotVersion;
    private List<ModelInfo> _availableModels = new();

    private readonly SystemMessageConfig _systemMessageConfig;

    private SystemMessageConfig CreateSystemMessage(string targetServer)
    {
        var targetInfo = targetServer.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "the local machine (localhost)"
            : $"the remote server: {targetServer}";

        return new SystemMessageConfig
        {
            Content = $"""
            You are TroubleScout, an expert Windows Server troubleshooting assistant. 
            Your role is to diagnose issues on Windows servers by analyzing system data and providing actionable insights.

            ## Target Server Context
            - You are currently connected to {targetInfo}
            - ALL commands and diagnostic operations will execute on this target server
            - When gathering data or making observations, you MUST always state which server the data comes from
            - Always verify that the data you receive is from the expected target server
            - If the user doesn't specify a server in their question, assume they mean the current target: {targetServer}

            ## Your Capabilities
            - Execute read-only PowerShell commands (Get-*) to gather diagnostic information from the target server
            - Analyze Windows Event Logs, services, processes, performance counters, disk space, and network configuration
            - Identify patterns, anomalies, and potential root causes
            - Provide clear, prioritized recommendations

            ## Troubleshooting Approach
            1. **Understand the Problem**: Ask clarifying questions if the issue description is vague
            2. **Gather Data**: Use the diagnostic tools to collect relevant information FROM THE TARGET SERVER
            3. **Verify Source**: Always confirm the data comes from {targetServer} by checking $env:COMPUTERNAME
            4. **Analyze**: Look for errors, warnings, resource exhaustion, or configuration issues
            5. **Diagnose**: Form hypotheses about the root cause based on evidence
            6. **Recommend**: Provide clear, actionable next steps

            ## Response Format
            - ALWAYS start your response by confirming which server you're analyzing (e.g., "Analyzing {targetServer}...")
            - Be concise but thorough
            - Use bullet points for lists
            - Highlight critical findings with **bold**
            - For remediation commands (non-Get commands), explain what they do and why they're needed
            - Always explain your reasoning
            - When presenting diagnostic data, include the source server name in your explanation

            ## Safety
            - Only read-only Get-* commands execute automatically
            - Any remediation commands (Start-, Stop-, Set-, Restart-, etc.) require user approval
            - Never suggest commands that could cause data loss without clear warnings
            - Always consider the impact of recommended actions

            Remember: Your goal is to help the user understand what's wrong with {targetServer} and guide them to a solution, 
            not just dump raw data. Interpret the findings and provide expert analysis. Always maintain awareness of which 
            server you're working on.
            """
        };
    }

    public TroubleshootingSession(string targetServer, string? model = null)
    {
        _targetServer = string.IsNullOrWhiteSpace(targetServer) ? "localhost" : targetServer;
        _requestedModel = model;
        _systemMessageConfig = CreateSystemMessage(_targetServer);
        _executor = new PowerShellExecutor(_targetServer);
        _diagnosticTools = new DiagnosticTools(_executor, PromptApprovalAsync, _targetServer);
    }

    private readonly string? _requestedModel;

    public string TargetServer => _targetServer;
    public string ConnectionMode => _executor.GetConnectionMode();
    public string SelectedModel => GetModelDisplayName(_selectedModel) ?? "default";
    public string CopilotVersion => _copilotVersion ?? "unknown";

    /// <summary>
    /// Initialize the session and establish connections
    /// </summary>
    public async Task<bool> InitializeAsync(Action<string>? updateStatus = null)
    {
        if (_isInitialized)
            return true;

        try
        {
            // Test PowerShell connection and verify target
            updateStatus?.Invoke($"Connecting to {_targetServer}...");
            
            var (connectionSuccess, connectionError) = await _executor.TestConnectionAsync();
            if (!connectionSuccess)
            {
                ConsoleUI.ShowError("Connection Failed", connectionError ?? $"Unable to connect to {_targetServer}");
                return false;
            }

            // Show verified connection
            updateStatus?.Invoke($"Connected to {_executor.ActualComputerName}...");

            // Initialize Copilot client
            updateStatus?.Invoke("Starting Copilot SDK...");
            
            // Find the SDK CLI path (bundled with @github/copilot-sdk npm package)
            var cliPath = GetCopilotCliPath();
            
            _copilotClient = new CopilotClient(new CopilotClientOptions
            {
                CliPath = cliPath,
                LogLevel = "info"
            });

            try
            {
                await _copilotClient.StartAsync();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("protocol version mismatch", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleUI.ShowError("Initialization Failed",
                    "SDK protocol version mismatch detected.\n\n" +
                    "Please update the Copilot CLI to match the SDK:\n" +
                    "  1. npm install -g @github/copilot@latest\n" +
                    "  2. npm install -g @github/copilot-sdk@latest\n" +
                    "  3. copilot auth login\n");
                _copilotClient = null;
                return false;
            }

            var authStatus = await _copilotClient.GetAuthStatusAsync();
            if (!authStatus.IsAuthenticated)
            {
                ConsoleUI.ShowError("Not Authenticated",
                    "Copilot CLI is not authenticated.\n\n" +
                    "Run: copilot auth login");
                return false;
            }

            updateStatus?.Invoke("Fetching available models...");
            _availableModels = await _copilotClient.ListModelsAsync();

            if (_availableModels.Count == 0)
            {
                ConsoleUI.ShowError("No Models Available", "No models were returned by the Copilot SDK. Ensure you are authenticated and your subscription has access to models.");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_requestedModel) && _availableModels.All(m => m.Id != _requestedModel))
            {
                ConsoleUI.ShowError("Invalid Model", $"The requested model '{_requestedModel}' is not available for your account.");
                return false;
            }

            // Create session with tools
            updateStatus?.Invoke("Creating AI session...");
            
            var config = new SessionConfig
            {
                Model = _requestedModel,
                SystemMessage = _systemMessageConfig,
                Streaming = true,
                Tools = _diagnosticTools.GetTools().ToList()
            };

            _copilotSession = await _copilotClient.CreateSessionAsync(config);
            
            // Subscribe to events to capture model info from usage events
            updateStatus?.Invoke("Detecting AI model...");
            
            var modelCaptured = new TaskCompletionSource<bool>();
            var sessionIdle = new TaskCompletionSource<bool>();
            using var subscription = _copilotSession.On(evt =>
            {
                switch (evt)
                {
                    case SessionStartEvent startEvt:
                        _selectedModel = startEvt.Data.SelectedModel;
                        _copilotVersion = startEvt.Data.CopilotVersion;
                        if (!string.IsNullOrEmpty(_selectedModel))
                            modelCaptured.TrySetResult(true);
                        break;
                    case SessionModelChangeEvent modelChange:
                        _selectedModel = modelChange.Data.NewModel;
                        modelCaptured.TrySetResult(true);
                        break;
                    case AssistantUsageEvent usageEvt:
                        // Usage events contain the actual model used
                        if (!string.IsNullOrEmpty(usageEvt.Data?.Model))
                        {
                            _selectedModel = usageEvt.Data.Model;
                            modelCaptured.TrySetResult(true);
                        }
                        break;
                    case SessionIdleEvent:
                        // Session finished processing
                        modelCaptured.TrySetResult(true);
                        sessionIdle.TrySetResult(true);
                        break;
                }
            });

            // Send a minimal message to get the model info from usage events
            await _copilotSession.SendAsync(new MessageOptions { Prompt = "Say 'ready' and nothing else." });
            
            // Wait for session to become idle (consume all streaming response)
            await Task.WhenAny(sessionIdle.Task, Task.Delay(15000));

            if (!string.IsNullOrWhiteSpace(_selectedModel))
            {
                SaveLastModel(_selectedModel);
            }
            
            _isInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError("Initialization Failed", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Change the AI model by creating a new session
    /// </summary>
    public async Task<bool> ChangeModelAsync(string newModel, Action<string>? updateStatus = null)
    {
        if (_copilotClient == null)
        {
            ConsoleUI.ShowError("Not Connected", "Copilot client not initialized");
            return false;
        }

        if (_availableModels.Count == 0 || _availableModels.All(m => m.Id != newModel))
        {
            ConsoleUI.ShowError("Invalid Model", $"The selected model '{newModel}' is not available for your account.");
            return false;
        }

        try
        {
            // Dispose existing session
            if (_copilotSession != null)
            {
                updateStatus?.Invoke("Closing current session...");
                await _copilotSession.DisposeAsync();
                _copilotSession = null;
            }

            // Create new session with the new model
            updateStatus?.Invoke($"Creating session with {newModel}...");
            
            var config = new SessionConfig
            {
                Model = newModel,
                SystemMessage = _systemMessageConfig,
                Streaming = true,
                Tools = _diagnosticTools.GetTools().ToList()
            };

            _copilotSession = await _copilotClient.CreateSessionAsync(config);
            
            // Capture model info
            updateStatus?.Invoke("Verifying model...");
            
            var modelCaptured = new TaskCompletionSource<bool>();
            var sessionIdle = new TaskCompletionSource<bool>();
            using var subscription = _copilotSession.On(evt =>
            {
                switch (evt)
                {
                    case SessionStartEvent startEvt:
                        _selectedModel = startEvt.Data.SelectedModel;
                        _copilotVersion = startEvt.Data.CopilotVersion;
                        if (!string.IsNullOrEmpty(_selectedModel))
                            modelCaptured.TrySetResult(true);
                        break;
                    case AssistantUsageEvent usageEvt:
                        if (!string.IsNullOrEmpty(usageEvt.Data?.Model))
                        {
                            _selectedModel = usageEvt.Data.Model;
                            modelCaptured.TrySetResult(true);
                        }
                        break;
                    case SessionIdleEvent:
                        modelCaptured.TrySetResult(true);
                        sessionIdle.TrySetResult(true);
                        break;
                }
            });

            await _copilotSession.SendAsync(new MessageOptions { Prompt = "Say 'ready' and nothing else." });
            
            // Wait for session to become idle (consume all streaming response)
            await Task.WhenAny(sessionIdle.Task, Task.Delay(15000));

            if (!string.IsNullOrWhiteSpace(_selectedModel))
            {
                SaveLastModel(_selectedModel);
            }

            return true;
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError("Model Change Failed", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Get the path to the Copilot CLI from the SDK package
    /// </summary>
    private static string GetCopilotCliPath()
    {
        // Check for COPILOT_CLI_PATH environment variable first
        var envPath = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        // Look for the CLI in common npm global locations
        var npmGlobalRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var possiblePaths = new[]
        {
            // npm global on Windows
            Path.Combine(npmGlobalRoot, "npm", "node_modules", "@github", "copilot-sdk", "node_modules", "@github", "copilot", "index.js"),
            Path.Combine(npmGlobalRoot, "npm", "node_modules", "@github", "copilot", "index.js"),
            // Fallback to just "copilot" in PATH
            "copilot"
        };

        foreach (var path in possiblePaths)
        {
            if (path == "copilot" || File.Exists(path))
            {
                return path;
            }
        }

        // Default to copilot in PATH
        return "copilot";
    }

    private string? GetModelDisplayName(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var model = _availableModels.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        return model?.Name ?? modelId;
    }

    /// <summary>
    /// Check if Copilot SDK CLI is available
    /// </summary>
    public static async Task<bool> CheckCopilotAvailableAsync()
    {
        try
        {
            var cliPath = GetCopilotCliPath();
            
            // If it's a .js file, verify node is available and the file exists
            if (cliPath.EndsWith(".js"))
            {
                if (!File.Exists(cliPath))
                    return false;
                    
                // Test node availability
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return false;
                
                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
            else
            {
                // Test copilot command directly
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {cliPath} --version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return false;
                
                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Send a message and process the response with streaming
    /// </summary>
    public async Task<bool> SendMessageAsync(string userMessage)
    {
        if (_copilotSession == null)
        {
            ConsoleUI.ShowError("Not Initialized", "Session not initialized. Call InitializeAsync first.");
            return false;
        }

        try
        {
            var done = new TaskCompletionSource<bool>();
            var hasError = false;
            var hasStartedStreaming = false;
            var processedDeltaIds = new HashSet<string>();
            
            // Create a live thinking indicator (manually disposed before recursive calls)
            var thinkingIndicator = ConsoleUI.CreateLiveThinkingIndicator();
            thinkingIndicator.Start();

            // Subscribe to session events for streaming (manually disposed before recursive calls)
            var subscription = _copilotSession.On(evt =>
            {
                switch (evt)
                {
                    case AssistantTurnStartEvent:
                        // AI has started processing
                        thinkingIndicator.UpdateStatus("Analyzing");
                        break;
                    
                    case ToolExecutionStartEvent toolStart:
                        // Show which tool is being executed
                        thinkingIndicator.ShowToolExecution(toolStart.Data?.ToolName ?? "diagnostic");
                        break;
                    
                    case ToolExecutionCompleteEvent:
                        // Tool finished, back to thinking
                        thinkingIndicator.UpdateStatus("Processing results");
                        break;
                    
                    case AssistantMessageDeltaEvent delta:
                        // Skip if we've already processed this event (deduplicate)
                        if (!processedDeltaIds.Add(delta.Id.ToString()))
                            break;
                        
                        // First streaming chunk - stop the spinner and start response
                        if (!hasStartedStreaming)
                        {
                            hasStartedStreaming = true;
                            thinkingIndicator.StopForResponse();
                            ConsoleUI.StartAIResponse();
                        }
                        // Streaming message chunk - print incrementally
                        ConsoleUI.WriteAIResponse(delta.Data?.DeltaContent ?? "");
                        break;
                    
                    case AssistantMessageEvent msg:
                        // Final message received (non-streaming fallback)
                        if (!hasStartedStreaming && !string.IsNullOrEmpty(msg.Data?.Content))
                        {
                            thinkingIndicator.StopForResponse();
                            ConsoleUI.StartAIResponse();
                            ConsoleUI.WriteAIResponse(msg.Data.Content);
                            hasStartedStreaming = true;
                        }
                        break;
                    
                    case SessionErrorEvent errorEvent:
                        thinkingIndicator.StopForResponse();
                        ConsoleUI.EndAIResponse();
                        ConsoleUI.ShowError("Session Error", errorEvent.Data?.Message ?? "Unknown error");
                        hasError = true;
                        done.TrySetResult(false);
                        break;
                    
                    case SessionIdleEvent:
                        // Session finished processing
                        done.TrySetResult(true);
                        break;
                }
            });

            // Send the message
            await _copilotSession.SendAsync(new MessageOptions { Prompt = userMessage });
            
            // Wait for completion
            await done.Task;
            
            // Explicitly dispose subscription BEFORE processing approvals
            // This prevents duplicate event handling when SendMessageAsync is called recursively
            subscription.Dispose();

            if (hasStartedStreaming)
            {
                ConsoleUI.EndAIResponse();
            }
            
            // Dispose thinking indicator before processing approvals
            thinkingIndicator.Dispose();

            // Handle any pending approval commands (may call SendMessageAsync recursively)
            if (!hasError)
            {
                await ProcessPendingApprovalsAsync();
            }

            return !hasError;
        }
        catch (Exception ex)
        {
            ConsoleUI.EndAIResponse();
            ConsoleUI.ShowError("Error", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Process any commands that require user approval
    /// </summary>
    private async Task ProcessPendingApprovalsAsync()
    {
        var pending = _diagnosticTools.PendingCommands;
        if (pending.Count == 0) return;

        var commands = pending.Select(p => (p.Command, p.Reason)).ToList();
        
        if (commands.Count == 1)
        {
            var cmd = commands[0];
            if (ConsoleUI.PromptCommandApproval(cmd.Command, cmd.Reason))
            {
                ConsoleUI.ShowInfo($"Executing: {cmd.Command}");
                var result = await _diagnosticTools.ExecuteApprovedCommandAsync(pending[0]);
                ConsoleUI.ShowSuccess("Command executed");
                
                // Feed result back to the AI
                await SendMessageAsync($"The approved command '{cmd.Command}' has been executed. Result:\n{result}\n\nPlease continue your analysis with this information.");
            }
            else
            {
                ConsoleUI.ShowWarning("Command skipped by user");
                _diagnosticTools.ClearPendingCommands();
            }
        }
        else
        {
            var approved = ConsoleUI.PromptBatchApproval(commands);
            
            foreach (var index in approved)
            {
                var cmd = pending[index - 1];
                ConsoleUI.ShowInfo($"Executing: {cmd.Command}");
                var result = await _diagnosticTools.ExecuteApprovedCommandAsync(cmd);
                ConsoleUI.ShowSuccess("Command executed");
            }

            _diagnosticTools.ClearPendingCommands();

            if (approved.Count > 0)
            {
                await SendMessageAsync("The approved commands have been executed. Please continue your analysis.");
            }
        }
    }

    /// <summary>
    /// Callback for command approval prompts
    /// </summary>
    private Task<bool> PromptApprovalAsync(string command, string reason)
    {
        return Task.FromResult(ConsoleUI.PromptCommandApproval(command, reason));
    }

    private static void SaveLastModel(string model)
    {
        var settings = AppSettingsStore.Load();
        settings.LastModel = model;
        AppSettingsStore.Save(settings);
    }

    /// <summary>
    /// Run the interactive session loop
    /// </summary>
    public async Task RunInteractiveLoopAsync()
    {
        while (true)
        {
            var input = ConsoleUI.GetUserInput().Trim();

            if (string.IsNullOrWhiteSpace(input))
                continue;

            // Handle commands
            var lowerInput = input.ToLowerInvariant();
            
            if (lowerInput is "/exit" or "/quit" or "exit" or "quit")
            {
                ConsoleUI.ShowInfo("Ending session. Goodbye!");
                break;
            }

            if (lowerInput == "/clear")
            {
                ConsoleUI.ShowBanner();
                ConsoleUI.ShowStatusPanel(_targetServer, ConnectionMode, true, SelectedModel);
                continue;
            }

            if (lowerInput == "/status")
            {
                ConsoleUI.ShowStatusPanel(_targetServer, ConnectionMode, _copilotSession != null, SelectedModel);
                continue;
            }

            if (lowerInput == "/help")
            {
                ConsoleUI.ShowWelcomeMessage();
                continue;
            }

            if (lowerInput == "/model")
            {
                var newModel = ConsoleUI.PromptModelSelection(SelectedModel, _availableModels);
                if (newModel != null && newModel != _selectedModel)
                {
                    var success = await ConsoleUI.RunWithSpinnerAsync($"Switching to {newModel}...", async updateStatus =>
                    {
                        return await ChangeModelAsync(newModel, updateStatus);
                    });
                    
                    if (success)
                    {
                        ConsoleUI.ShowSuccess($"Now using model: {SelectedModel}");
                    }
                }
                continue;
            }

            if (lowerInput.StartsWith("/connect"))
            {
                var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    ConsoleUI.ShowWarning("Usage: /connect <server>");
                }
                else
                {
                    var newServer = parts[1];
                    ConsoleUI.ShowWarning("Connecting to a different server requires restarting TroubleScout.");
                    ConsoleUI.ShowInfo($"Run: dotnet run -- --server {newServer}");
                }
                continue;
            }

            // Send message to Copilot
            await SendMessageAsync(input);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_copilotSession != null)
        {
            await _copilotSession.DisposeAsync();
        }

        if (_copilotClient != null)
        {
            try
            {
                await _copilotClient.DisposeAsync();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("protocol version mismatch", StringComparison.OrdinalIgnoreCase))
            {
                // Ignore protocol mismatch during cleanup when startup failed
            }
        }

        _executor.Dispose();
        
        GC.SuppressFinalize(this);
    }
}
