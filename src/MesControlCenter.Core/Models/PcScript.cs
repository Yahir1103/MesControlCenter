namespace MesControlCenter.Core.Models;

public class PcScript
{
    public long Id { get; set; }
    public long PcId { get; set; }
    public string ScriptName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public string? ExtraStatus { get; set; }
    public DateTime? CreatedAt { get; set; }
}
