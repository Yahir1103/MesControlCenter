using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MesControlCenter.Core.Services;

/// <summary>
/// Frees a TCP port by killing whatever process is listening on it. Used as a
/// pre-start step to avoid EADDRINUSE when a previous run left the port held.
/// Uses netstat (native, no deps). ponytail: netstat parse, fine for occasional
/// pre-start use; switch to GetExtendedTcpTable P/Invoke only if this gets hot.
/// </summary>
public static class PortUtil
{
    // Matches a LISTENING line and captures the local port and PID:
    //   TCP    0.0.0.0:3012    0.0.0.0:0    LISTENING    1234
    private static readonly Regex ListenLine = new(
        @"^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Kills every process LISTENING on <paramref name="port"/>. Returns the PIDs
    /// it terminated. Returns empty if the port was free or on any error.
    /// </summary>
    public static IReadOnlyList<int> FreePort(int port, Action<string>? log = null)
    {
        if (port <= 0 || port > 65535) return Array.Empty<int>();

        var pids = FindListeningPids(port);
        var killed = new List<int>();
        foreach (var pid in pids)
        {
            if (pid <= 4) continue; // 0/4 = System/Idle, never kill
            if (TryKill(pid))
            {
                killed.Add(pid);
                log?.Invoke($"Liberado puerto {port}: terminado PID {pid}");
            }
        }
        if (killed.Count == 0)
            log?.Invoke($"Puerto {port} ya estaba libre");
        return killed;
    }

    private static HashSet<int> FindListeningPids(int port)
    {
        var result = new HashSet<int>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return result;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            foreach (var raw in output.Split('\n'))
            {
                var m = ListenLine.Match(raw);
                if (m.Success && int.Parse(m.Groups[1].Value) == port)
                    result.Add(int.Parse(m.Groups[2].Value));
            }
        }
        catch
        {
            // netstat unavailable / parse error → treat as no PIDs found.
        }
        return result;
    }

    private static bool TryKill(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return false;
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(3000);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
