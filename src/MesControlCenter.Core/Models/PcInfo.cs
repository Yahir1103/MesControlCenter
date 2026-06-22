namespace MesControlCenter.Core.Models;

public class PcInfo
{
    public long Id { get; set; }
    public string PcKey { get; set; } = string.Empty;
    public string PcName { get; set; } = string.Empty;
    public string Role { get; set; } = "USER";
    public bool IsActive { get; set; }
    public DateTime? LastSeen { get; set; }
    public int? SecondsSinceSeen { get; set; }
    public DateTime? CreatedAt { get; set; }
}
