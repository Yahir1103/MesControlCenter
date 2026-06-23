using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MesControlCenter.Core.Models;
using MesControlCenter.Core.Services;

namespace MesControlCenter.UI.ViewModels;

public partial class ScriptEditorViewModel : EditorViewModelBase
{
    // ── General ───────────────────────────────────────────────────
    [ObservableProperty] private string _scriptName   = string.Empty;
    [ObservableProperty] private string _scriptPath   = string.Empty;
    [ObservableProperty] private string _arguments    = string.Empty;
    [ObservableProperty] private string _workDir      = string.Empty;
    [ObservableProperty] private string _interpreter  = PythonDetector.Detect();
    [ObservableProperty] private bool   _isExeMode;
    [ObservableProperty] private bool   _isPs1FileMode;

    // ── Deployment (Git) ──────────────────────────────────────────
    [ObservableProperty] private bool   _gitDeployEnabled;
    [ObservableProperty] private string _gitRepoDir = string.Empty;
    [ObservableProperty] private string _gitBranch = string.Empty;
    [ObservableProperty] private string _gitPostPullCommand = string.Empty;
    [ObservableProperty] private string _gitHealthTimeout = "60";
    [ObservableProperty] private bool   _gitRollbackOnFailure = true;

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

    public override void LoadFrom(ScriptEntry entry)
    {
        ScriptName   = entry.Name;
        ScriptPath   = entry.Path;
        Arguments    = entry.Args;
        WorkDir      = entry.WorkDir;
        Interpreter  = string.IsNullOrEmpty(entry.Interpreter) ? PythonDetector.Detect() : entry.Interpreter;
        LoadCommonFrom(entry);

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

    public override ScriptEntry? ToScriptEntry(string? existingId = null)
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

        ErrorMessage = string.Empty;
        var entry = new ScriptEntry
        {
            Id         = existingId ?? _id,
            Name       = ScriptName.Trim(),
            Path       = ScriptPath.Trim(),
            Args       = Arguments.Trim(),
            WorkDir    = wd,
            Interpreter = interp,

            GitDeployEnabled = GitDeployEnabled,
            GitRepoDir = repoDir,
            GitBranch = GitBranch.Trim(),
            GitPostPullCommand = GitPostPullCommand.Trim(),
            GitHealthTimeoutSeconds = ParseInt(GitHealthTimeout, 60),
            GitRollbackOnFailure = GitRollbackOnFailure,
        };
        ApplyCommonTo(entry);
        return entry;
    }
}
