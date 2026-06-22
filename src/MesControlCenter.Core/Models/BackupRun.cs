namespace MesControlCenter.Core.Models;

public class BackupRun
{
    public long Id { get; set; }
    public string RunType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int? DurationMs { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? FilePath { get; set; }
    public string? ErrorMessage { get; set; }
}
