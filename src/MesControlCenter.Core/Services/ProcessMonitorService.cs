using System.Diagnostics;
using System.Management;
using MesControlCenter.Core.Interfaces;
using MesControlCenter.Core.Models;

namespace MesControlCenter.Core.Services;

public class ProcessMonitorService : IScriptMonitor
{
    private List<string> _scripts = new();
    private Dictionary<int, string>? _cmdLineCache;
    private DateTime _lastCacheTime = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);

    public void UpdateScripts(List<string> scripts)
    {
        _scripts = scripts;
        Log($"[MONITOR] Updated with {scripts.Count} scripts to monitor");
    }

    public bool IsScriptRunning(string scriptName)
    {
        var status = GetScriptStatus(scriptName);
        return status.IsActive;
    }

    public ScriptStatus GetScriptStatus(string scriptName)
    {
        try
        {
            var scriptLower = scriptName.ToLower();
            var isExe = scriptLower.EndsWith(".exe");
            var cmdLines = GetCommandLineCache();

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var procNameLower = proc.ProcessName.ToLower();
                    bool matched = false;

                    if (isExe)
                    {
                        var exeName = scriptLower.EndsWith(".exe")
                            ? scriptLower[..^4]
                            : scriptLower;
                        matched = procNameLower == exeName;
                    }
                    else
                    {
                        if (procNameLower.Contains("python"))
                        {
                            if (cmdLines.TryGetValue(proc.Id, out var cmdLine))
                            {
                                matched = cmdLine.ToLower().Contains(scriptLower);
                            }
                        }
                    }

                    if (matched)
                    {
                        double memoryMb = proc.WorkingSet64 / (1024.0 * 1024.0);

                        return new ScriptStatus
                        {
                            ScriptName = scriptName,
                            IsActive = true,
                            Pid = proc.Id,
                            CpuPercent = 0, // CPU% requires two samples; simplified
                            MemoryMb = Math.Round(memoryMb, 2),
                            Status = "running"
                        };
                    }
                }
                catch (Exception)
                {
                    // Access denied or process exited
                }
                finally
                {
                    proc.Dispose();
                }
            }

            return new ScriptStatus
            {
                ScriptName = scriptName,
                IsActive = false,
                Status = "stopped"
            };
        }
        catch (Exception ex)
        {
            Log($"[MONITOR] [ERROR] Failed to get status for {scriptName}: {ex.Message}");
            return new ScriptStatus
            {
                ScriptName = scriptName,
                IsActive = false,
                Status = "error"
            };
        }
    }

    public IList<ScriptStatus> GetAllStatuses()
    {
        // Refresh cache once for the whole batch
        _cmdLineCache = null;
        return _scripts.Select(GetScriptStatus).ToList();
    }

    public bool AreAllScriptsActive()
    {
        if (_scripts.Count == 0) return false;
        var statuses = GetAllStatuses();
        var activeCount = statuses.Count(s => s.IsActive);
        Log($"[MONITOR] Scripts active: {activeCount}/{_scripts.Count}");
        return activeCount == _scripts.Count;
    }

    private Dictionary<int, string> GetCommandLineCache()
    {
        if (_cmdLineCache != null && DateTime.Now - _lastCacheTime < CacheTtl)
            return _cmdLineCache;

        _cmdLineCache = new Dictionary<int, string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE 'python%'");
            foreach (var obj in searcher.Get())
            {
                var pid = Convert.ToInt32(obj["ProcessId"]);
                var cmdLine = obj["CommandLine"]?.ToString() ?? "";
                _cmdLineCache[pid] = cmdLine;
            }
        }
        catch (Exception ex)
        {
            Log($"[MONITOR] [WARN] WMI query failed: {ex.Message}");
        }

        _lastCacheTime = DateTime.Now;
        return _cmdLineCache;
    }

    private static void Log(string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        Console.WriteLine($"[{ts}] {message}");
    }
}
