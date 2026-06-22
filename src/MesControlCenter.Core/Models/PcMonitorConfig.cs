namespace MesControlCenter.Core.Models;

public class PcMonitorConfig
{
    public string PcKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string PcName { get; set; } = string.Empty;
    public List<string> Scripts { get; set; } = new();
    public string? CreatedAt { get; set; }
}
