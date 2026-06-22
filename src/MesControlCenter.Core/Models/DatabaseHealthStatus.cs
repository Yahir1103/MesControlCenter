namespace MesControlCenter.Core.Models;

public sealed class DatabaseHealthStatus
{
    public bool IsConfigured { get; set; }
    public bool IsOnline { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime CheckedAt { get; set; } = DateTime.Now;
}
