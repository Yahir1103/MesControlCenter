namespace MesControlCenter.Core.Models;

public class ScriptStatus
{
    public string ScriptName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int? Pid { get; set; }
    public double? CpuPercent { get; set; }
    public double? MemoryMb { get; set; }
    public string Status { get; set; } = "stopped";
}
