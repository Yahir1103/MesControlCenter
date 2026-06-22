namespace MesControlCenter.Core.Models;

public class BackupConfig
{
    public bool Enabled { get; set; } = true;
    public string BackupTime { get; set; } = "22:00";
    public int RetentionDays { get; set; } = 7;
    public string BackupDir { get; set; } = "./backups";
    public string Timezone { get; set; } = "America/Mexico_City";
}
