namespace MesControlCenter.Core.Models;

public sealed record RunningScriptProcess(string EntryId, string Name, int RootProcessId);

public sealed record ProcessResourceUsage(
    string EntryId,
    string ScriptName,
    string ProcessName,
    int ProcessId,
    int RootProcessId,
    double CpuPercent,
    double MemoryMb);

public sealed record ResourceMonitorSnapshot(
    double? TemperatureC,
    string TemperatureSource,
    IReadOnlyList<ProcessResourceUsage> Processes,
    double SystemCpuPercent,
    double SystemRamPercent,
    double SystemRamUsedMb,
    double SystemRamTotalMb);
