using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MesControlCenter.Core.Models;

namespace MesControlCenter.UI.ViewModels;

public partial class NpmCommandEditorViewModel : ObservableObject
{
    // ── General ───────────────────────────────────────────────────
    [ObservableProperty] private string _commandName      = string.Empty;
    [ObservableProperty] private string _folder           = string.Empty;
    [ObservableProperty] private string _npmWorkDir       = string.Empty;
    [ObservableProperty] private string _npmScript        = string.Empty;
    [ObservableProperty] private string _packageJsonStatus = string.Empty;
    [ObservableProperty] private bool   _autoStart;

    // ── Reliability ───────────────────────────────────────────────
    [ObservableProperty] private bool   _autoRestart;
    [ObservableProperty] private string _restartDelay   = "5";
    [ObservableProperty] private string _maxAttempts    = "3";
    [ObservableProperty] private bool   _healthCheckEnabled;
    [ObservableProperty] private string _healthCheckUrl = string.Empty;
    [ObservableProperty] private string _healthInterval = "30";
    [ObservableProperty] private string _healthFailures = "3";

    // ── Hooks ─────────────────────────────────────────────────────
    [ObservableProperty] private string _freePort        = string.Empty;
    [ObservableProperty] private string _preStartCommand = string.Empty;
    [ObservableProperty] private string _postStopCommand = string.Empty;

    [ObservableProperty] private string _errorMessage = string.Empty;

    // Generated once so repeated ToScriptEntry() calls are idempotent (same Id).
    private readonly string _id = Guid.NewGuid().ToString();

    public ObservableCollection<string> AvailableScripts { get; } = new();
    public ObservableCollection<string> AvailableFolders { get; } = new();

    public void SetAvailableFolders(IEnumerable<string> folders)
    {
        AvailableFolders.Clear();
        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            AvailableFolders.Add(folder.Trim());
    }

    partial void OnNpmWorkDirChanged(string value) => LoadPackageJsonScripts();

    partial void OnNpmScriptChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(CommandName) && !string.IsNullOrWhiteSpace(value))
            CommandName = $"npm run {value}";
    }

    [RelayCommand]
    private void BrowseDirectory()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select project folder containing package.json"
        };
        if (dlg.ShowDialog() == true)
            NpmWorkDir = dlg.FolderName;
    }

    private void LoadPackageJsonScripts()
    {
        AvailableScripts.Clear();
        PackageJsonStatus = string.Empty;

        if (string.IsNullOrWhiteSpace(NpmWorkDir)) return;

        var pkgPath = Path.Combine(NpmWorkDir, "package.json");
        if (!File.Exists(pkgPath))
        {
            PackageJsonStatus = "package.json not found in this directory.";
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pkgPath));
            if (doc.RootElement.TryGetProperty("scripts", out var scripts))
            {
                foreach (var s in scripts.EnumerateObject())
                    AvailableScripts.Add(s.Name);
                PackageJsonStatus = $"{AvailableScripts.Count} script(s) found in package.json";
            }
            else
            {
                PackageJsonStatus = "No 'scripts' section found in package.json.";
            }
        }
        catch (Exception ex)
        {
            PackageJsonStatus = $"Error reading package.json: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(NpmScript) && AvailableScripts.Count > 0)
            NpmScript = AvailableScripts[0];
    }

    public void LoadFrom(ScriptEntry entry)
    {
        CommandName    = entry.Name;
        Folder         = entry.Folder;
        NpmWorkDir     = entry.WorkDir;
        NpmScript      = entry.NpmScript;
        AutoStart      = entry.AutoStart;

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

        // Trigger package.json load for existing entry
        LoadPackageJsonScripts();
    }

    public ScriptEntry? ToScriptEntry(string? existingId = null)
    {
        if (string.IsNullOrWhiteSpace(CommandName))
        {
            ErrorMessage = "Name is required.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(NpmWorkDir) || !Directory.Exists(NpmWorkDir))
        {
            ErrorMessage = "Please select a valid project directory.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(NpmScript))
        {
            ErrorMessage = "Please specify an npm script to run.";
            return null;
        }

        ErrorMessage = string.Empty;
        return new ScriptEntry
        {
            Id        = existingId ?? _id,
            Kind      = "npm",
            Name      = CommandName.Trim(),
            Folder    = Folder.Trim(),
            WorkDir   = NpmWorkDir,
            NpmScript = NpmScript.Trim(),
            AutoStart = AutoStart,

            AutoRestart         = AutoRestart,
            RestartDelaySeconds = ParseInt(RestartDelay, 5),
            MaxRestartAttempts  = ParseInt(MaxAttempts, 3),

            HealthCheckEnabled               = HealthCheckEnabled,
            HealthCheckUrl                   = HealthCheckUrl.Trim(),
            HealthCheckIntervalSeconds       = ParseInt(HealthInterval, 30),
            HealthCheckFailuresBeforeRestart = ParseInt(HealthFailures, 3),

            FreePort        = ParseInt(FreePort, 0),
            PreStartCommand = PreStartCommand.Trim(),
            PostStopCommand = PostStopCommand.Trim(),
        };
    }

    private static int ParseInt(string value, int fallback)
        => int.TryParse(value, out var n) ? Math.Max(0, n) : fallback;
}
