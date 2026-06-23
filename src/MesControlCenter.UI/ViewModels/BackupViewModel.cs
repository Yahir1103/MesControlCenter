using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MesControlCenter.Core.Models;
using MesControlCenter.Core.Services;

namespace MesControlCenter.UI.ViewModels;

public partial class BackupViewModel : ObservableObject
{
    private static readonly Regex TimeRegex = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);
    private readonly LocalBackupService _backupService;
    private readonly DispatcherTimer _runningTimer;
    private DateTime? _runningStartedAt;
    private bool _subscribed;

    public ObservableCollection<BackupRunViewModel> Runs { get; } = new();

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _backupTime = "22:00";
    [ObservableProperty] private string _retentionDaysText = "7";
    [ObservableProperty] private string _backupDir = string.Empty;
    [ObservableProperty] private string _timezone = string.Empty;
    [ObservableProperty] private string _dbHost = "localhost";
    [ObservableProperty] private string _dbPortText = "3306";
    [ObservableProperty] private string _dbUser = string.Empty;
    [ObservableProperty] private string _dbPassword = string.Empty;
    [ObservableProperty] private string _dbDatabase = string.Empty;
    [ObservableProperty] private string _mysqldumpPath = "mysqldump";
    [ObservableProperty] private string _statusMessage = "Loading local backup service...";
    [ObservableProperty] private string _backupStateText = "Unknown";
    [ObservableProperty] private SolidColorBrush _backupStateColor = Brush("#7d8590");
    [ObservableProperty] private string _nextRunText = "-";
    [ObservableProperty] private string _lastRunText = "-";
    [ObservableProperty] private string _lastDurationText = "-";
    [ObservableProperty] private string _lastSizeText = "-";
    [ObservableProperty] private string _lastFilePath = string.Empty;
    [ObservableProperty] private string _lastError = string.Empty;
    [ObservableProperty] private bool _isBackupRunning;
    [ObservableProperty] private string _runningBackupText = string.Empty;

    public BackupViewModel(LocalBackupService backupService)
    {
        _backupService = backupService;
        _runningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _runningTimer.Tick += (_, _) => UpdateRunningBackupText();
    }

    public async Task StartAsync()
    {
        try
        {
            Subscribe();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup service error: {ex.Message}";
            BackupStateText = "Error";
            BackupStateColor = Brush("#ef4444");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task StopAsync()
    {
        if (_subscribed)
        {
            _backupService.BackupUpdated -= OnBackupUpdated;
            _subscribed = false;
        }

        _runningTimer.Stop();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Loading backup status...";
            var status = await _backupService.GetStatusAsync();
            ApplyStatus(status);
            await LoadRunsAsync();
            StatusMessage = "Backup status loaded.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load backups: {ex.Message}";
            BackupStateText = "Error";
            BackupStateColor = Brush("#ef4444");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BrowseMysqldumpPath()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select mysqldump.exe",
            Filter = "mysqldump.exe|mysqldump.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        var initialDirectory = GetExistingDirectory(MysqldumpPath);
        if (!string.IsNullOrWhiteSpace(initialDirectory))
            dlg.InitialDirectory = initialDirectory;

        if (dlg.ShowDialog() == true)
        {
            MysqldumpPath = dlg.FileName;
            StatusMessage = "mysqldump selected.";
        }
    }

    [RelayCommand]
    private void BrowseBackupDir()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select backup folder",
            Multiselect = false
        };

        var initialDirectory = GetExistingDirectory(BackupDir);
        if (!string.IsNullOrWhiteSpace(initialDirectory))
            dlg.InitialDirectory = initialDirectory;

        if (dlg.ShowDialog() == true)
        {
            BackupDir = dlg.FolderName;
            StatusMessage = "Backup folder selected.";
        }
    }

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        if (!ValidateInputs(out var retentionDays, out var dbPort))
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "Saving backup configuration...";
            var saved = await _backupService.UpdateConfigAsync(new BackupConfig
            {
                Enabled = Enabled,
                BackupTime = BackupTime.Trim(),
                RetentionDays = retentionDays,
                BackupDir = BackupDir.Trim(),
                Timezone = string.IsNullOrWhiteSpace(Timezone) ? TimeZoneInfo.Local.Id : Timezone.Trim(),
                DbHost = DbHost.Trim(),
                DbPort = dbPort,
                DbUser = DbUser.Trim(),
                DbPassword = DbPassword,
                DbDatabase = DbDatabase.Trim(),
                MysqldumpPath = MysqldumpPath.Trim()
            });

            ApplyConfig(saved);
            var status = await _backupService.GetStatusAsync();
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
            var started = await _backupService.StartManualBackupAsync();
            StatusMessage = started
                ? "Manual backup started. The status will update when it finishes."
                : "A backup is already running.";
            var status = await _backupService.GetStatusAsync();
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

    private bool ValidateInputs(out int retentionDays, out int dbPort)
    {
        retentionDays = 0;
        dbPort = 0;

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

        var backupDir = CleanPathInput(BackupDir);
        if (string.IsNullOrWhiteSpace(backupDir))
        {
            StatusMessage = "Backup folder is required.";
            return false;
        }

        try
        {
            _ = Path.GetFullPath(backupDir);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup folder path is not valid: {ex.Message}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DbHost))
        {
            StatusMessage = "Database host is required.";
            return false;
        }

        if (!int.TryParse(DbPortText.Trim(), out dbPort) || dbPort < 1 || dbPort > 65535)
        {
            StatusMessage = "Database port must be between 1 and 65535.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DbUser))
        {
            StatusMessage = "Database user is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DbDatabase))
        {
            StatusMessage = "Database name is required.";
            return false;
        }

        var mysqldumpPath = CleanPathInput(MysqldumpPath);
        if (string.IsNullOrWhiteSpace(mysqldumpPath))
        {
            StatusMessage = "mysqldump path is required.";
            return false;
        }

        BackupTime = time;
        BackupDir = backupDir;
        DbHost = DbHost.Trim();
        DbPortText = dbPort.ToString();
        DbUser = DbUser.Trim();
        DbDatabase = DbDatabase.Trim();
        MysqldumpPath = mysqldumpPath;
        return true;
    }

    private void Subscribe()
    {
        if (_subscribed)
            return;
        _backupService.BackupUpdated += OnBackupUpdated;
        _subscribed = true;
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
        var runs = await _backupService.GetRunsAsync();
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
        ApplyRunningState(status);

        var last = status.LastRun;
        BackupStateText = status.IsRunning
            ? "Running"
            : last?.Status?.ToUpperInvariant() switch
            {
                "SUCCESS" => "Healthy",
                "FAILED" => "Failed",
                "SKIPPED" => "Skipped",
                _ => "Ready"
            };

        BackupStateColor = status.IsRunning
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

    private void ApplyRunningState(BackupStatus status)
    {
        IsBackupRunning = status.IsRunning;
        _runningStartedAt = status.RunningRun?.StartedAt;

        if (status.IsRunning)
        {
            UpdateRunningBackupText();
            if (!_runningTimer.IsEnabled)
                _runningTimer.Start();
        }
        else
        {
            _runningTimer.Stop();
            RunningBackupText = string.Empty;
            _runningStartedAt = null;
        }
    }

    private void ApplyConfig(BackupConfig config)
    {
        Enabled = config.Enabled;
        BackupTime = string.IsNullOrWhiteSpace(config.BackupTime) ? "22:00" : config.BackupTime;
        RetentionDaysText = Math.Max(1, config.RetentionDays).ToString();
        BackupDir = config.BackupDir;
        Timezone = config.Timezone;
        DbHost = config.DbHost;
        DbPortText = config.DbPort.ToString();
        DbUser = config.DbUser;
        DbPassword = config.DbPassword;
        DbDatabase = config.DbDatabase;
        MysqldumpPath = config.MysqldumpPath;
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

    private void UpdateRunningBackupText()
    {
        if (!IsBackupRunning)
        {
            RunningBackupText = string.Empty;
            return;
        }

        if (_runningStartedAt == null)
        {
            RunningBackupText = "Backup running...";
            return;
        }

        var elapsed = DateTime.Now - _runningStartedAt.Value;
        RunningBackupText = $"Elapsed {FormatElapsed(elapsed)} · Started {_runningStartedAt:yyyy-MM-dd HH:mm:ss}";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
            return $"{(int)elapsed.TotalHours:0}h {elapsed.Minutes:00}m {elapsed.Seconds:00}s";
        if (elapsed.TotalMinutes >= 1)
            return $"{elapsed.Minutes:0}m {elapsed.Seconds:00}s";
        return $"{Math.Max(0, elapsed.Seconds):0}s";
    }

    private static SolidColorBrush Brush(string color)
        => new((Color)ColorConverter.ConvertFromString(color));

    private static string CleanPathInput(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return Environment.ExpandEnvironmentVariables(value.Trim().Trim('"', '\''));
    }

    private static string? GetExistingDirectory(string? value)
    {
        var path = CleanPathInput(value);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            if (Directory.Exists(path))
                return Path.GetFullPath(path);

            var parent = Path.GetDirectoryName(path);
            while (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
                parent = Path.GetDirectoryName(parent);

            return string.IsNullOrWhiteSpace(parent) ? null : parent;
        }
        catch
        {
            return null;
        }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
