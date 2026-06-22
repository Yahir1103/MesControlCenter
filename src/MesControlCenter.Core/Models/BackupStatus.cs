namespace MesControlCenter.Core.Models;

public class BackupStatus
{
    public BackupConfig Config { get; set; } = new();
    public bool IsRunning { get; set; }
    public DateTime? NextRunAt { get; set; }
    public BackupRun? LastRun { get; set; }
}
