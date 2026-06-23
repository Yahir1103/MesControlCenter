using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MesControlCenter.Core.Models;
using MesControlCenter.Core.Services;

namespace MesControlCenter.UI.ViewModels;

public partial class ScriptEditorViewModel : ObservableObject
{
    // ── General ───────────────────────────────────────────────────
    [ObservableProperty] private string _scriptName   = string.Empty;
    [ObservableProperty] private string _folder       = string.Empty;
    [ObservableProperty] private string _scriptPath   = string.Empty;
    [ObservableProperty] private string _arguments    = string.Empty;
    [ObservableProperty] private string _workDir      = string.Empty;
    [ObservableProperty] private string _interpreter  = PythonDetector.Detect();
    [ObservableProperty] private bool   _autoStart;
    [ObservableProperty] private bool   _isExeMode;
    [ObservableProperty] private bool   _isPs1FileMode;
    [ObservableProperty] private string _errorMessage = string.Empty;

    // ── Reliability ───────────────────────────────────────────────
    [ObservableProperty] private bool   _autoRestart;
    [ObservableProperty] private string _restartDelay    = "5";
    [ObservableProperty] private string _maxAttempts     = "3";
    [ObservableProperty] private bool   _healthCheckEnabled;
    [ObservableProperty] private string _healthCheckUrl  = string.Empty;
    [ObservableProperty] private string _healthInterval  = "30";
    [ObservableProperty] private string _healthFailures  = "3";

    // ── Hooks ─────────────────────────────────────────────────────
    [ObservableProperty] private string _freePort         = string.Empty;
    [ObservableProperty] private string _preStartCommand  = string.Empty;
    [ObservableProperty] private string _postStopCommand  = string.Empty;
    [ObservableProperty] private bool   _gitDeployEnabled;
    [ObservableProperty] private string _gitRepoDir = string.Empty;
    [ObservableProperty] private string _gitBranch = string.Empty;
    [ObservableProperty] private string _gitPostPullCommand = string.Empty;
    [ObservableProperty] private string _gitHealthTimeout = "60";
    [ObservableProperty] private bool   _gitRollbackOnFailure = true;

    // Generated once so repeated ToScriptEntry() calls are idempotent (same Id).
    private readonly string _id = Guid.NewGuid().ToString();

    public ObservableCollection<string> AvailableFolders { get; } = new();

    public void SetAvailableFolders(IEnumerable<string> folders)
    {
        AvailableFolders.Clear();
        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            AvailableFolders.Add(folder.Trim());
    }

    partial void OnScriptPathChanged(string value)
    {
        IsExeMode    = value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || value.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                    || value.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        IsPs1FileMode = value.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(ScriptName) && !string.IsNullOrWhiteSpace(value))
            ScriptName = Path.GetFileNameWithoutExtension(value);
        if (string.IsNullOrWhiteSpace(WorkDir) && !string.IsNullOrWhiteSpace(value))
            WorkDir = Path.GetDirectoryName(value) ?? "";
    }

    partial void OnWorkDirChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(GitRepoDir) && !string.IsNullOrWhiteSpace(value))
            GitRepoDir = value;
    }

    public void LoadFrom(ScriptEntry entry)
    {
        ScriptName   = entry.Name;
        Folder       = entry.Folder;
        ScriptPath   = entry.Path;
        Arguments    = entry.Args;
        WorkDir      = entry.WorkDir;
        Interpreter  = string.IsNullOrEmpty(entry.Interpreter) ? PythonDetector.Detect() : entry.Interpreter;
        AutoStart    = entry.AutoStart;

        AutoRestart    = entry.AutoRestart;
        RestartDelay   = entry.RestartDelaySeconds.ToString();
        MaxAttempts    = entry.MaxRestartAttempts.ToString();

        HealthCheckEnabled = entry.HealthCheckEnabled;
        HealthCheckUrl     = entry.HealthCheckUrl;
        HealthInterval     = entry.HealthCheckIntervalSeconds.ToString();
        HealthFailures     = entry.HealthCheckFailuresBeforeRestart.ToString();

        FreePort        = entry.FreePort > 0 ? entry.FreePort.ToString() : string.Empty;
        PreStartCommand = entry.PreStartCommand;
        PostStopCommand = entry.PostStopCommand;
        GitDeployEnabled = entry.GitDeployEnabled;
        GitRepoDir = string.IsNullOrWhiteSpace(entry.GitRepoDir) ? entry.WorkDir : entry.GitRepoDir;
        GitBranch = entry.GitBranch;
        GitPostPullCommand = entry.GitPostPullCommand;
        GitHealthTimeout = entry.GitHealthTimeoutSeconds.ToString();
        GitRollbackOnFailure = entry.GitRollbackOnFailure;
    }

    [RelayCommand]
    private void AutoDetectPython()
    {
        Interpreter = PythonDetector.Detect();
    }

    public ScriptEntry? ToScriptEntry(string? existingId = null)
    {
        if (string.IsNullOrWhiteSpace(ScriptName) || string.IsNullOrWhiteSpace(ScriptPath))
        {
            ErrorMessage = "Name and script path are required.";
            return null;
        }

        var pathLower = ScriptPath.ToLower();
        if (!pathLower.EndsWith(".py")  && !pathLower.EndsWith(".exe")
         && !pathLower.EndsWith(".bat") && !pathLower.EndsWith(".cmd")
         && !pathLower.EndsWith(".ps1"))
        {
            ErrorMessage = "Please select a .py, .exe, .bat, .cmd, or .ps1 file.";
            return null;
        }

        if (!File.Exists(ScriptPath))
        {
            ErrorMessage = "Script/executable path does not exist.";
            return null;
        }

        var wd = string.IsNullOrWhiteSpace(WorkDir) ? Path.GetDirectoryName(ScriptPath) ?? "" : WorkDir;
        var interp = (IsExeMode || IsPs1FileMode) ? "" : Interpreter;
        var repoDir = string.IsNullOrWhiteSpace(GitRepoDir) ? wd : GitRepoDir.Trim();

        if (GitDeployEnabled)
        {
            if (string.IsNullOrWhiteSpace(repoDir))
            {
                ErrorMessage = "Git repository directory is required when deploy is enabled.";
                return null;
            }

            if (!Directory.Exists(repoDir))
            {
                ErrorMessage = "Git repository directory does not exist.";
                return null;
            }

            if (string.IsNullOrWhiteSpace(GitBranch))
            {
                ErrorMessage = "Git branch is required when deploy is enabled.";
                return null;
            }
        }

        return new ScriptEntry
        {
            Id         = existingId ?? _id,
            Name       = ScriptName.Trim(),
            Folder     = Folder.Trim(),
            Path       = ScriptPath.Trim(),
            Args       = Arguments.Trim(),
            WorkDir    = wd,
            Interpreter = interp,
            AutoStart  = AutoStart,

            AutoRestart         = AutoRestart,
            RestartDelaySeconds = ParseInt(RestartDelay, 5),
            MaxRestartAttempts  = ParseInt(MaxAttempts, 3),

            HealthCheckEnabled              = HealthCheckEnabled,
            HealthCheckUrl                  = HealthCheckUrl.Trim(),
            HealthCheckIntervalSeconds      = ParseInt(HealthInterval, 30),
            HealthCheckFailuresBeforeRestart = ParseInt(HealthFailures, 3),

            FreePort        = ParseInt(FreePort, 0),
            PreStartCommand = PreStartCommand.Trim(),
            PostStopCommand = PostStopCommand.Trim(),
            GitDeployEnabled = GitDeployEnabled,
            GitRepoDir = repoDir,
            GitBranch = GitBranch.Trim(),
            GitPostPullCommand = GitPostPullCommand.Trim(),
            GitHealthTimeoutSeconds = ParseInt(GitHealthTimeout, 60),
            GitRollbackOnFailure = GitRollbackOnFailure,
        };
    }

    private static int ParseInt(string value, int fallback)
        => int.TryParse(value, out var n) ? Math.Max(0, n) : fallback;
}
