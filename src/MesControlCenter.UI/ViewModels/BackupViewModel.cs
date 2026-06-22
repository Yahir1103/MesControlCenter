using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MesControlCenter.Core.Models;
using MesControlCenter.Core.Services;

namespace MesControlCenter.UI.ViewModels;

public partial class BackupViewModel : ObservableObject
{
    private static readonly Regex TimeRegex = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);
    private readonly WsDashboardClient _ws = new();

    public ObservableCollection<BackupRunViewModel> Runs { get; } = new();

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _backupTime = "22:00";
    [ObservableProperty] private string _retentionDaysText = "7";
    [ObservableProperty] private string _backupDir = string.Empty;
    [ObservableProperty] private string _timezone = string.Empty;
    [ObservableProperty] private string _statusMessage = "Connecting to server...";
    [ObservableProperty] private string _serverStateText = "Unknown";
    [ObservableProperty] private SolidColorBrush _serverStateColor = Brush("#7d8590");
    [ObservableProperty] private string _nextRunText = "-";
    [ObservableProperty] private string _lastRunText = "-";
    [ObservableProperty] private string _lastDurationText = "-";
    [ObservableProperty] private string _lastSizeText = "-";
    [ObservableProperty] private string _lastFilePath = string.Empty;
    [ObservableProperty] private string _lastError = string.Empty;

    public BackupViewModel()
    {
        _ws.BackupUpdated += OnBackupUpdated;
        _ws.Disconnected += reason => OnUi(() =>
        {
            StatusMessage = $"Disconnected: {reason}";
            ServerStateText = "Disconnected";
            ServerStateColor = Brush("#ef4444");
        });
    }

    public async Task StartAsync()
    {
        try
        {
            var url = ClientConfig.ResolveServerUrl();
            await _ws.ConnectForBackupsAsync(url);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection error: {ex.Message}";
            ServerStateText = "Error";
            ServerStateColor = Brush("#ef4444");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task StopAsync() => await _ws.DisposeAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!_ws.IsConnected)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "Loading backup status...";
            var status = await _ws.GetBackupStatusAsync();
            ApplyStatus(status);
            await LoadRunsAsync();
            StatusMessage = "Backup status loaded.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load backups: {ex.Message}";
            ServerStateText = "Error";
            ServerStateColor = Brush("#ef4444");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        if (!ValidateInputs(out var retentionDays))
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "Saving backup configuration...";
            var saved = await _ws.UpdateBackupConfigAsync(new BackupConfig
            {
                Enabled = Enabled,
                BackupTime = BackupTime.Trim(),
                RetentionDays = retentionDays,
                BackupDir = BackupDir.Trim()
            });

            ApplyConfig(saved);
            var status = await _ws.GetBackupStatusAsync();
            ApplyStatus(status);
            StatusMessage = "Backup configuration saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save configuration: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RunNowAsync()
    {
        if (!_ws.IsConnected)
            return;

        var result = MessageBox.Show(
            "Start a database backup now?",
            "Run Backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "Starting manual backup...";
            var started = await _ws.RunBackupNowAsync();
            StatusMessage = started
                ? "Manual backup started. The status will update when it finishes."
                : "A backup is already running.";
            var status = await _ws.GetBackupStatusAsync();
            ApplyStatus(status);
            await LoadRunsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not start backup: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool ValidateInputs(out int retentionDays)
    {
        retentionDays = 0;
        var time = BackupTime.Trim();
        if (!TimeRegex.IsMatch(time))
        {
            StatusMessage = "Backup time must use HH:mm format, for example 22:00.";
            return false;
        }

        if (!int.TryParse(RetentionDaysText.Trim(), out retentionDays) || retentionDays < 1)
        {
            StatusMessage = "Retention days must be a positive number.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(BackupDir))
        {
            StatusMessage = "Backup folder is required.";
            return false;
        }

        BackupTime = time;
        BackupDir = BackupDir.Trim();
        return true;
    }

    private void OnBackupUpdated(BackupStatus status)
    {
        OnUi(() =>
        {
            ApplyStatus(status);
            _ = LoadRunsSafeAsync();
        });
    }

    private async Task LoadRunsSafeAsync()
    {
        try
        {
            await LoadRunsAsync();
        }
        catch (Exception ex)
        {
            OnUi(() => StatusMessage = $"Could not refresh backup history: {ex.Message}");
        }
    }

    private async Task LoadRunsAsync()
    {
        var runs = await _ws.GetBackupRunsAsync();
        OnUi(() =>
        {
            Runs.Clear();
            foreach (var run in runs)
                Runs.Add(new BackupRunViewModel(run));
        });
    }

    private void ApplyStatus(BackupStatus status)
    {
        ApplyConfig(status.Config);

        var last = status.LastRun;
        ServerStateText = status.IsRunning
            ? "Running"
            : last?.Status?.ToUpperInvariant() switch
            {
                "SUCCESS" => "Healthy",
                "FAILED" => "Failed",
                "SKIPPED" => "Skipped",
                _ => "Ready"
            };

        ServerStateColor = status.IsRunning
            ? Brush("#58a6ff")
            : last?.Status?.ToUpperInvariant() switch
            {
                "SUCCESS" => Brush("#10b981"),
                "FAILED" => Brush("#ef4444"),
                "SKIPPED" => Brush("#d29922"),
                _ => Brush("#7d8590")
            };

        NextRunText = status.NextRunAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        LastRunText = last == null
            ? "No backups yet"
            : $"{last.Status.ToUpperInvariant()} at {FormatDate(last.FinishedAt ?? last.StartedAt)}";
        LastDurationText = FormatDuration(last?.DurationMs);
        LastSizeText = FormatSize(last?.FileSizeBytes);
        LastFilePath = last?.FilePath ?? string.Empty;
        LastError = last?.ErrorMessage ?? string.Empty;
    }

    private void ApplyConfig(BackupConfig config)
    {
        Enabled = config.Enabled;
        BackupTime = string.IsNullOrWhiteSpace(config.BackupTime) ? "22:00" : config.BackupTime;
        RetentionDaysText = Math.Max(1, config.RetentionDays).ToString();
        BackupDir = config.BackupDir;
        Timezone = config.Timezone;
    }

    private static string FormatDate(DateTime? value)
        => value?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

    private static string FormatDuration(int? milliseconds)
    {
        if (milliseconds == null) return "-";
        var span = TimeSpan.FromMilliseconds(milliseconds.Value);
        return span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.0} min" : $"{span.TotalSeconds:0.0} s";
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

    private static SolidColorBrush Brush(string color)
        => new((Color)ColorConverter.ConvertFromString(color));

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
