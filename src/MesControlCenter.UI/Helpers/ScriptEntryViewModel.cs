using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MesControlCenter.Core.Models;
using MesControlCenter.Core.Services;

namespace MesControlCenter.UI.Helpers;

public partial class ScriptEntryViewModel : ObservableObject
{
    private readonly ScriptEntry _entry;

    public ScriptEntryViewModel(ScriptEntry entry)
    {
        _entry = entry;
        _statusText = "Idle";
        _statusIcon = App.Current.FindResource("IdleIcon") as DrawingImage;
    }

    public ScriptEntry Entry => _entry;
    public string Id   => _entry.Id;
    public string Name => _entry.Name;
    public string Args => _entry.Args;
    public string WorkDir => _entry.WorkDir;
    public string Folder => _entry.Folder;
    public string FolderPath
    {
        get
        {
            if (_entry.IsPsCommand)
                return string.Empty;

            if (_entry.IsNpmCommand)
                return _entry.WorkDir;

            if (!string.IsNullOrWhiteSpace(_entry.WorkDir))
                return _entry.WorkDir;

            return string.IsNullOrWhiteSpace(_entry.Path)
                ? string.Empty
                : System.IO.Path.GetDirectoryName(_entry.Path) ?? string.Empty;
        }
    }

    public string FolderGroupKey
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_entry.Folder))
                return _entry.Folder.Trim().ToUpperInvariant();

            if (_entry.IsPsCommand)
                return "PowerShell";

            if (_entry.IsNpmCommand && string.IsNullOrWhiteSpace(FolderPath))
                return "npm";

            return string.IsNullOrWhiteSpace(FolderPath) ? "Uncategorized" : FolderPath;
        }
    }

    public string FolderName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_entry.Folder))
                return _entry.Folder.Trim();

            if (_entry.IsPsCommand)
                return "PowerShell";

            if (_entry.IsNpmCommand && string.IsNullOrWhiteSpace(FolderPath))
                return "npm";

            if (string.IsNullOrWhiteSpace(FolderPath))
                return "Uncategorized";

            var trimmed = FolderPath.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);

            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? trimmed : name;
        }
    }

    public string Path => _entry.IsPsCommand   ? "[PowerShell Command]"
                        : _entry.IsNpmCommand  ? $"npm run {_entry.NpmScript}"
                        : _entry.Path;

    public string InterpreterDisplay =>
          _entry.IsPsCommand  ? (_entry.RunAsAdmin ? "PowerShell (Admin)" : "PowerShell")
        : _entry.IsNpmCommand ? "Node.js (npm)"
        : _entry.IsBatchFile  ? "Batch Script (cmd.exe)"
        : _entry.IsPsFile     ? "PowerShell (.ps1)"
        : _entry.IsExecutable ? "Native Executable"
        : (string.IsNullOrEmpty(_entry.Interpreter) ? PythonDetector.Detect() : _entry.Interpreter);

    public string WorkDirDisplay =>
          (_entry.IsPsCommand)  ? "—"
        : (_entry.IsNpmCommand) ? _entry.WorkDir
        : string.IsNullOrEmpty(_entry.WorkDir)
            ? System.IO.Path.GetDirectoryName(_entry.Path) ?? ""
            : _entry.WorkDir;

    public bool CanGitDeploy => _entry.GitDeployEnabled && !_entry.IsPsCommand && !_entry.IsNpmCommand;
    public bool CanTriggerGitDeploy => CanGitDeploy && !IsDeploying;

    [ObservableProperty] private string _healthStatus = string.Empty;
    [ObservableProperty] private bool _isDeploying;
    [ObservableProperty] private string _gitCommitText = string.Empty;
    [ObservableProperty] private bool _isGitCommitLoading;

    public bool ShowGitCommit => CanGitDeploy
        && (IsGitCommitLoading || !string.IsNullOrWhiteSpace(GitCommitText));

    public bool AutoStart
    {
        get => _entry.AutoStart;
        set
        {
            if (_entry.AutoStart != value)
            {
                _entry.AutoStart = value;
                OnPropertyChanged();
            }
        }
    }

    public string? LastRun
    {
        get => _entry.LastRun;
        set
        {
            _entry.LastRun = value;
            OnPropertyChanged();
        }
    }

    public int? LastExitCode
    {
        get => _entry.LastExitCode;
        set
        {
            _entry.LastExitCode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExitCodeDisplay));
        }
    }

    public string ExitCodeDisplay => LastExitCode?.ToString() ?? "";

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private DrawingImage? _statusIcon;

    [ObservableProperty]
    private bool _isRunning;

    // Left accent bar color for the card
    public SolidColorBrush StatusAccentBrush
    {
        get
        {
            if (IsRunning)
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34d399"));
            if (StatusText.StartsWith("Exit"))
            {
                var code = LastExitCode ?? -1;
                return code == 0
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4a5568"))
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f87171"));
            }
            if (StatusText == "Error")
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#fbbf24"));
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2a3347"));
        }
    }

    // Status badge background
    public SolidColorBrush StatusBadgeBg
    {
        get
        {
            if (IsRunning)
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0d2818"));
            if (StatusText.StartsWith("Exit"))
            {
                var code = LastExitCode ?? -1;
                return code == 0
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#141422"))
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d1215"));
            }
            if (StatusText == "Error")
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d2006"));
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111520"));
        }
    }

    // Status badge text color
    public SolidColorBrush StatusBadgeColor
    {
        get
        {
            if (IsRunning)
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34d399"));
            if (StatusText.StartsWith("Exit"))
            {
                var code = LastExitCode ?? -1;
                return code == 0
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7b8ba8"))
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f87171"));
            }
            if (StatusText == "Error")
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#fbbf24"));
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4a5568"));
        }
    }

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(StatusAccentBrush));
        OnPropertyChanged(nameof(StatusBadgeBg));
        OnPropertyChanged(nameof(StatusBadgeColor));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusAccentBrush));
        OnPropertyChanged(nameof(StatusBadgeBg));
        OnPropertyChanged(nameof(StatusBadgeColor));
    }

    partial void OnIsDeployingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanTriggerGitDeploy));
    }

    partial void OnGitCommitTextChanged(string value)
    {
        OnPropertyChanged(nameof(ShowGitCommit));
    }

    partial void OnIsGitCommitLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowGitCommit));
    }

    public void SetStatus(string status, string iconKey)
    {
        StatusText = status;
        StatusIcon = App.Current.FindResource(iconKey) as DrawingImage;
        IsRunning = status.StartsWith("Running");
    }
}
