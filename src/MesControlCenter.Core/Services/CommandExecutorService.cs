using System.Diagnostics;
using System.Management;
using System.Text.Json;
using MesControlCenter.Core.Models;

namespace MesControlCenter.Core.Services;

public class CommandExecutorService
{
    private static readonly string AgentCommandFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".script_control_center", "agent_commands.json");

    private List<ScriptEntry> _scriptEntries = new();

    public void UpdateEntries(List<ScriptEntry> entries)
    {
        _scriptEntries = entries;
    }

    public (bool Success, string Message) ExecuteCommand(string command, string? payloadJson)
    {
        Log($"[EXECUTOR] Executing command: {command}");

        try
        {
            return command switch
            {
                "RESTART_SCRIPT" => RestartScript(payloadJson),
                "PING" => Ping(),
                "UPDATE_AGENT" => UpdateAgent(),
                _ => (false, $"Unknown command: {command}")
            };
        }
        catch (Exception ex)
        {
            var msg = $"Command execution error: {ex.Message}";
            Log($"[EXECUTOR] [ERROR] {msg}");
            return (false, msg);
        }
    }

    private (bool Success, string Message) RestartScript(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
            return (false, "Missing payload");

        string? scriptName;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            scriptName = doc.RootElement.GetProperty("script_name").GetString();
        }
        catch
        {
            return (false, "Invalid payload: missing script_name");
        }

        if (string.IsNullOrEmpty(scriptName))
            return (false, "Missing script_name in payload");

        Log($"[EXECUTOR] Attempting to restart: {scriptName}");
        var scriptLower = scriptName.ToLower();
        var scriptFileLower = Path.GetFileName(scriptLower);

        // Step 1: Kill existing processes.
        // Filter at the WMI level (host process name) instead of scanning every
        // process on the system, then confirm the match on the command line so we
        // never kill an unrelated process that merely mentions the script name.
        int killedCount = 0;
        try
        {
            // Resolve the launcher process the script runs under: python.exe for
            // .py scripts, node.exe for .js, otherwise the script's own exe name.
            string? hostProcessName = Path.GetExtension(scriptFileLower) switch
            {
                ".py"  => "python.exe",
                ".pyw" => "pythonw.exe",
                ".js"  => "node.exe",
                ".exe" => scriptFileLower,
                _      => null
            };

            // Build a targeted WMI query. If we know the host process, filter on
            // it; otherwise fall back to matching the exe name directly.
            var nameFilter = hostProcessName ?? scriptFileLower;
            var wql = "SELECT ProcessId, Name, CommandLine FROM Win32_Process " +
                      $"WHERE Name = '{nameFilter.Replace("'", "''")}'";

            using var searcher = new ManagementObjectSearcher(wql);

            foreach (var obj in searcher.Get())
            {
                var cmdLine = obj["CommandLine"]?.ToString()?.ToLower() ?? "";
                var pid = Convert.ToInt32(obj["ProcessId"]);

                // For host-launched scripts (python/node), require the command
                // line to reference the script file. For direct exe matches the
                // Name filter is already sufficient.
                bool isDirectExe = hostProcessName == scriptFileLower;
                bool cmdLineMatches = cmdLine.Contains(scriptFileLower);

                if (isDirectExe || cmdLineMatches)
                {
                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                        killedCount++;
                        Log($"[EXECUTOR] Terminated PID {pid}");
                    }
                    catch (Exception)
                    {
                        // Process already exited
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[EXECUTOR] [WARN] Process kill phase error: {ex.Message}");
        }

        // Step 2: Signal Control Center to relaunch via agent_commands.json
        if (!TryAppendSignal(new
            {
                action = "restart_script",
                script_name = scriptName,
                timestamp = DateTime.Now.ToString("o")
            }, out var signalError))
        {
            return (false, $"Failed to write restart signal: {signalError}");
        }

        var msg = $"Script {scriptName}: killed {killedCount} process(es), restart signal sent to Control Center";
        Log($"[EXECUTOR] {msg}");
        return (true, msg);
    }

    private (bool Success, string Message) UpdateAgent()
    {
        // The agent cannot replace its own running binary; it signals the
        // Control Center (which is watching agent_commands.json) to pull the
        // latest agent build and relaunch it. This mirrors RESTART_SCRIPT.
        Log("[EXECUTOR] UPDATE_AGENT requested");

        if (!TryAppendSignal(new
            {
                action = "update_agent",
                timestamp = DateTime.Now.ToString("o")
            }, out var signalError))
        {
            return (false, $"Failed to write update signal: {signalError}");
        }

        var msg = "Update signal sent to Control Center; agent will be redeployed";
        Log($"[EXECUTOR] {msg}");
        return (true, msg);
    }

    /// <summary>
    /// Appends a signal object to the shared agent_commands.json file that the
    /// Control Center polls. Returns false (with an error message) on failure.
    /// </summary>
    private static bool TryAppendSignal(object signal, out string error)
    {
        error = string.Empty;
        try
        {
            var dir = Path.GetDirectoryName(AgentCommandFile)!;
            Directory.CreateDirectory(dir);

            List<object> existing = new();
            if (File.Exists(AgentCommandFile))
            {
                try
                {
                    var content = File.ReadAllText(AgentCommandFile);
                    var arr = JsonSerializer.Deserialize<List<JsonElement>>(content);
                    if (arr != null)
                        existing = arr.Cast<object>().ToList();
                }
                catch { }
            }

            existing.Add(signal);

            var json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AgentCommandFile, json);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private (bool Success, string Message) Ping()
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        var msg = $"Pong! Agent is alive at {ts}";
        Log($"[EXECUTOR] PING received");
        return (true, msg);
    }

    private static void Log(string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        Console.WriteLine($"[{ts}] {message}");
    }
}
