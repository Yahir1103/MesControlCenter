namespace MesControlCenter.Core.Models;

public class BackupConfig
{
    public bool Enabled { get; set; } = true;
    public string BackupTime { get; set; } = "22:00";
    public int RetentionDays { get; set; } = 7;
    public string BackupDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".script_control_center",
        "backups");
    public string Timezone { get; set; } = TimeZoneInfo.Local.Id;
    public string DbHost { get; set; } = "localhost";
    public int DbPort { get; set; } = 3306;
    public string DbUser { get; set; } = string.Empty;
    public string DbPassword { get; set; } = string.Empty;
    public string DbDatabase { get; set; } = string.Empty;
    public string MysqldumpPath { get; set; } = "mysqldump";
}
