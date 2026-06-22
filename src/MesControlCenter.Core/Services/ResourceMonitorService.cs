using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using MesControlCenter.Core.Models;

namespace MesControlCenter.Core.Services;

public sealed class ResourceMonitorService
{
    private static readonly TimeSpan TemperatureCacheTtl = TimeSpan.FromSeconds(10);

    // System-wide CPU% from GetSystemTimes deltas (cheap, no warmup/admin needed).
    private ulong _prevIdle, _prevKernel, _prevUser;
    private double _lastSystemCpuPercent;

    private readonly Dictionary<int, ProcessCpuSample> _processCpuSamples = new();
    private DateTime _lastTemperatureSampleAt = DateTime.MinValue;
    private double? _cachedTemperatureC;
    private string _cachedTemperatureSource = "No sensor";
    private Computer? _hardwareMonitor;
    private bool _hardwareMonitorOpened;
    private bool _hardwareMonitorFailed;

    // LibreHardwareMonitor is NOT thread-safe: Open()/Update() mutate shared
    // driver state. All access to _hardwareMonitor must hold this lock so the
    // 3s resource timer and the 30s temperature timer never run it concurrently.
    private readonly object _hardwareLock = new();

    // Guards the cached temperature fields, which are written by the temperature
    // read and read by Capture() from a different timer/thread.
    private readonly object _cacheLock = new();

    public (double? ValueC, string Source) ReadTemperatureSnapshot()
    {
        var reading = ReadTemperature(DateTime.UtcNow);
        return (reading.ValueC, reading.Source);
    }

    public int TerminateProcessTree(int rootProcessId)
    {
        var pids = CollectProcessTree(rootProcessId, ReadProcessTree());
        RunTaskKill(rootProcessId);

        var terminated = 0;
        foreach (var pid in pids.Where(pid => pid != rootProcessId).Append(rootProcessId))
        {
            if (TryKillProcess(pid))
                terminated++;
        }

        return terminated;
    }

    public int TerminateLikelyNpmProcesses(string workDir, string npmScript)
    {
        var normalizedWorkDir = NormalizeCommandLineFragment(workDir);
        var normalizedScript = NormalizeCommandLineFragment($"npm run {npmScript}");
        if (string.IsNullOrWhiteSpace(normalizedWorkDir) && string.IsNullOrWhiteSpace(normalizedScript))
            return 0;

        var killed = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, CommandLine FROM Win32_Process " +
                "WHERE Name = 'node.exe' OR Name = 'npm.exe'");

            foreach (ManagementObject obj in searcher.Get())
            {
                var commandLine = NormalizeCommandLineFragment(obj["CommandLine"]?.ToString() ?? string.Empty);
                if (string.IsNullOrWhiteSpace(commandLine))
                    continue;

                var matchesWorkDir = !string.IsNullOrWhiteSpace(normalizedWorkDir)
                    && commandLine.Contains(normalizedWorkDir, StringComparison.OrdinalIgnoreCase);
                var matchesScript = !string.IsNullOrWhiteSpace(normalizedScript)
                    && commandLine.Contains(normalizedScript, StringComparison.OrdinalIgnoreCase);

                if (!matchesWorkDir && !matchesScript)
                    continue;

                var pid = Convert.ToInt32(obj["ProcessId"]);
                if (TryKillProcess(pid))
                    killed++;
            }
        }
        catch
        {
        }

        return killed;
    }

    /// <summary>
    /// Measures CPU% and RAM for each running script by summing the whole process
    /// tree (the script's root process plus every descendant). This matters for
    /// launchers like cmd.exe/npm where the real work runs in child node.exe
    /// processes — measuring only the root reports ~0 and shows nothing.
    /// </summary>
    public ResourceMonitorSnapshot Capture(IReadOnlyCollection<RunningScriptProcess> runningScripts)
    {
        var sampledAt = DateTime.UtcNow;
        var usages = new List<ProcessResourceUsage>();

        if (runningScripts.Count > 0)
        {
            // One parent→children map per capture (not per script). This is the
            // only reliable way to find descendants on Windows.
            var childrenByParent = ReadProcessTree();
            var liveRootPids = new HashSet<int>();

            foreach (var script in runningScripts)
            {
                try
                {
                    var usage = MeasureScriptTree(script, childrenByParent, sampledAt);
                    if (usage != null)
                    {
                        usages.Add(usage);
                        liveRootPids.Add(script.RootProcessId);
                    }
                }
                catch
                {
                    // The process may have exited mid-capture; just skip it.
                }
            }

            PurgeStaleCpuSamples(liveRootPids);
        }
        else
        {
            _processCpuSamples.Clear();
        }

        var cachedTemp = ReadCachedTemperature();
        var (ramUsedMb, ramTotalMb, ramPct) = ReadSystemRam();
        return new ResourceMonitorSnapshot(
            cachedTemp.ValueC,
            cachedTemp.Source,
            usages
                .OrderByDescending(s => s.CpuPercent)
                .ThenByDescending(s => s.MemoryMb)
                .ToList(),
            ReadSystemCpuPercent(),
            ramPct, ramUsedMb, ramTotalMb);
    }

    // ── System-wide CPU & RAM (P/Invoke, no deps, no admin) ──

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    private static ulong ToU64(FILETIME ft) => ((ulong)ft.High << 32) | ft.Low;

    private double ReadSystemCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return _lastSystemCpuPercent;

        ulong i = ToU64(idle), k = ToU64(kernel), u = ToU64(user);
        ulong idleDelta = i - _prevIdle, kernelDelta = k - _prevKernel, userDelta = u - _prevUser;
        _prevIdle = i; _prevKernel = k; _prevUser = u;

        // kernel time INCLUDES idle. busy = (kernel+user) - idle ; total = kernel+user.
        ulong total = kernelDelta + userDelta;
        if (total == 0) return _lastSystemCpuPercent;
        _lastSystemCpuPercent = Math.Round(Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100), 1);
        return _lastSystemCpuPercent;
    }

    private static (double usedMb, double totalMb, double pct) ReadSystemRam()
    {
        var m = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        double totalMb = m.TotalPhys / 1024d / 1024d;
        double usedMb = (m.TotalPhys - m.AvailPhys) / 1024d / 1024d;
        return (Math.Round(usedMb), Math.Round(totalMb), m.MemoryLoad);
    }

    private ProcessResourceUsage? MeasureScriptTree(
        RunningScriptProcess script,
        IReadOnlyDictionary<int, List<int>> childrenByParent,
        DateTime sampledAt)
    {
        // Collect the root + all descendants.
        var treePids = CollectProcessTree(script.RootProcessId, childrenByParent);

        double memoryMb = 0;
        TimeSpan totalCpuTime = TimeSpan.Zero;
        int liveCount = 0;
        string rootProcessName = "process";

        foreach (var pid in treePids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited)
                    continue;

                p.Refresh();
                // Private bytes (not working set) so the number tracks Task
                // Manager's "Memory" column, which excludes shared memory.
                memoryMb += p.PrivateMemorySize64 / 1024d / 1024d;
                totalCpuTime += p.TotalProcessorTime;
                liveCount++;

                if (pid == script.RootProcessId)
                    rootProcessName = p.ProcessName;
            }
            catch
            {
                // Process exited between tree enumeration and measurement.
            }
        }

        if (liveCount == 0)
            return null;

        // CPU% from the delta of summed CPU time across the whole tree.
        var cpuPercent = CalculateTreeCpuPercent(script.RootProcessId, totalCpuTime, sampledAt);

        return new ProcessResourceUsage(
            script.EntryId,
            script.Name,
            liveCount > 1 ? $"{rootProcessName} +{liveCount - 1}" : rootProcessName,
            script.RootProcessId,
            script.RootProcessId,
            Math.Round(Math.Clamp(cpuPercent, 0, 100 * Environment.ProcessorCount), 1),
            Math.Round(memoryMb, 1));
    }

    private double CalculateTreeCpuPercent(int rootPid, TimeSpan totalProcessorTime, DateTime sampledAt)
    {
        var cpuPercent = 0d;

        if (_processCpuSamples.TryGetValue(rootPid, out var previous))
        {
            var cpuDelta = totalProcessorTime - previous.TotalProcessorTime;
            var elapsedMs = (sampledAt - previous.SampledAt).TotalMilliseconds;

            // Percent of a SINGLE core (like Task Manager's per-process feel): a
            // script pegging one core shows ~100%, two cores ~200%. Not divided by
            // core count — that made a maxed core read as 100/N % and looked broken.
            if (cpuDelta > TimeSpan.Zero && elapsedMs > 0)
                cpuPercent = cpuDelta.TotalMilliseconds / elapsedMs * 100d;
        }

        _processCpuSamples[rootPid] = new ProcessCpuSample(totalProcessorTime, sampledAt);
        return cpuPercent;
    }

    private void PurgeStaleCpuSamples(HashSet<int> livePids)
    {
        foreach (var pid in _processCpuSamples.Keys.Where(k => !livePids.Contains(k)).ToList())
            _processCpuSamples.Remove(pid);
    }

    private TemperatureReading ReadTemperature(DateTime sampledAt)
    {
        lock (_cacheLock)
        {
            if (sampledAt - _lastTemperatureSampleAt < TemperatureCacheTtl)
                return new TemperatureReading(_cachedTemperatureC, _cachedTemperatureSource);
            _lastTemperatureSampleAt = sampledAt;
        }

        double? value;
        string source;

        if (TryReadHardwareTemperatureC() is { } hardwareTemperature)
        {
            value = hardwareTemperature;
            source = "Hardware sensor";
        }
        else if (TryReadWmiTemperatureC() is { } wmiTemperature)
        {
            value = wmiTemperature;
            source = "WMI sensor";
        }
        else
        {
            value = null;
            // Most temp sensors need admin; "No sensor" while unelevated misleads.
            source = IsElevated() ? "No sensor" : "Requiere admin";
        }

        lock (_cacheLock)
        {
            _cachedTemperatureC = value;
            _cachedTemperatureSource = source;
        }
        return new TemperatureReading(value, source);
    }

    private TemperatureReading ReadCachedTemperature()
    {
        lock (_cacheLock)
            return new TemperatureReading(_cachedTemperatureC, _cachedTemperatureSource);
    }

    private double? TryReadHardwareTemperatureC()
    {
        if (_hardwareMonitorFailed)
            return null;

        // Serialize every interaction with LibreHardwareMonitor — it is not
        // thread-safe and concurrent Update() calls can hang the kernel driver.
        lock (_hardwareLock)
        {
            try
            {
                // Only enable the CPU. Opening GPU/motherboard/controller drivers
                // is what makes the first read take seconds and freeze the UI;
                // for a "global temp" the CPU package sensor is enough.
                // CPU + motherboard: many boards only expose a temp via the
                // Super-I/O (motherboard) sensor, not the CPU package. GPU/
                // controller stay off (slow to open, not needed for a temp).
                _hardwareMonitor ??= new Computer
                {
                    IsCpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsGpuEnabled = false,
                    IsControllerEnabled = false
                };

                if (!_hardwareMonitorOpened)
                {
                    _hardwareMonitor.Open();
                    _hardwareMonitorOpened = true;
                }

                var temperatures = new List<double>();
                var visited = new HashSet<IHardware>(ReferenceEqualityComparer.Instance);
                foreach (var hardware in _hardwareMonitor.Hardware)
                    ReadHardwareTemperatures(hardware, temperatures, visited);

                return temperatures.Count == 0
                    ? null
                    : Math.Round(temperatures.Average(), 1);
            }
            catch
            {
                _hardwareMonitorFailed = true;
                return null;
            }
        }
    }

    private static void ReadHardwareTemperatures(
        IHardware hardware,
        ICollection<double> temperatures,
        ISet<IHardware> visited)
    {
        if (!visited.Add(hardware))
            return;

        hardware.Update();

        foreach (var subHardware in hardware.SubHardware)
            ReadHardwareTemperatures(subHardware, temperatures, visited);

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || sensor.Value is not { } value)
                continue;

            // Reject 0/near-0: LibreHardwareMonitor returns 0 for sensors it can't
            // actually read (typically when not running elevated). A real CPU temp
            // is never ~0 C, so treat it as "no reading" and fall back to WMI.
            if (value is > 5 and < 125)
                temperatures.Add(value);
        }
    }

    private static double? TryReadWmiTemperatureC()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            var temperatures = new List<double>();
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["CurrentTemperature"] == null)
                    continue;

                var raw = Convert.ToDouble(obj["CurrentTemperature"]);
                var celsius = (raw - 2732d) / 10d;
                if (celsius is > -30 and < 125)
                    temperatures.Add(celsius);
            }

            return temperatures.Count == 0
                ? null
                : Math.Round(temperatures.Average(), 1);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<int, List<int>> ReadProcessTree()
    {
        var byParent = new Dictionary<int, List<int>>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId FROM Win32_Process");

            foreach (ManagementObject obj in searcher.Get())
            {
                var processId = Convert.ToInt32(obj["ProcessId"]);
                var parentProcessId = Convert.ToInt32(obj["ParentProcessId"]);

                if (!byParent.TryGetValue(parentProcessId, out var children))
                {
                    children = new List<int>();
                    byParent[parentProcessId] = children;
                }

                children.Add(processId);
            }
        }
        catch
        {
        }

        return byParent;
    }

    private static bool TryKillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return false;

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RunTaskKill(int processId)
    {
        try
        {
            using var taskKill = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/PID {processId} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            taskKill?.WaitForExit(5000);
        }
        catch
        {
        }
    }

    private static string NormalizeCommandLineFragment(string value)
        => value
            .Trim()
            .Trim('"')
            .Replace('\\', '/')
            .TrimEnd('/')
            .ToLowerInvariant();

    private static HashSet<int> CollectProcessTree(
        int rootProcessId,
        IReadOnlyDictionary<int, List<int>> processTree)
    {
        var result = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(rootProcessId);

        while (pending.Count > 0)
        {
            var pid = pending.Pop();
            if (!result.Add(pid))
                continue;

            if (!processTree.TryGetValue(pid, out var children))
                continue;

            foreach (var childPid in children)
                pending.Push(childPid);
        }

        return result;
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private sealed record ProcessCpuSample(TimeSpan TotalProcessorTime, DateTime SampledAt);

    private sealed record TemperatureReading(double? ValueC, string Source);
}
