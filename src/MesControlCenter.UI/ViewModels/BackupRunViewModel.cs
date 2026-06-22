using MesControlCenter.Core.Models;

namespace MesControlCenter.UI.ViewModels;

public class BackupRunViewModel
{
    public BackupRunViewModel(BackupRun data)
    {
        Data = data;
    }

    public BackupRun Data { get; }

    public long Id => Data.Id;
    public string RunType => string.IsNullOrWhiteSpace(Data.RunType) ? "-" : Data.RunType;
    public string Status => string.IsNullOrWhiteSpace(Data.Status) ? "-" : Data.Status.ToUpperInvariant();
    public string StartedAt => FormatDate(Data.StartedAt);
    public string FinishedAt => FormatDate(Data.FinishedAt);
    public string Duration => FormatDuration(Data.DurationMs);
    public string Size => FormatSize(Data.FileSizeBytes);
    public string FilePath => Data.FilePath ?? string.Empty;
    public string Error => Data.ErrorMessage ?? string.Empty;

    private static string FormatDate(DateTime? value)
        => value?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

    private static string FormatDuration(int? milliseconds)
    {
        if (milliseconds == null) return "-";
        var span = TimeSpan.FromMilliseconds(milliseconds.Value);
        if (span.TotalMinutes >= 1)
            return $"{span.TotalMinutes:0.0} min";
        return $"{span.TotalSeconds:0.0} s";
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes == null) return "-";
        var value = (double)bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
