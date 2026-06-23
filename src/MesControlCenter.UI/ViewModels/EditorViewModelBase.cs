using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MesControlCenter.Core.Models;

namespace MesControlCenter.UI.ViewModels;

/// <summary>
/// Shared surface for the three script-editor ViewModels (Script / PowerShell / npm):
/// the Reliability, Health, and Hooks fields plus folder list and Id are identical
/// across all three. Subclasses add their type-specific fields and map them in
/// LoadFrom / ToScriptEntry, calling LoadCommonFrom / ApplyCommonTo for this block.
/// </summary>
public abstract partial class EditorViewModelBase : ObservableObject
{
    // ── Shared: General ───────────────────────────────────────────
    [ObservableProperty] private string _folder    = string.Empty;
    [ObservableProperty] private bool   _autoStart;

    // ── Shared: Reliability ───────────────────────────────────────
    [ObservableProperty] private bool   _autoRestart;
    [ObservableProperty] private string _restartDelay   = "5";
    [ObservableProperty] private string _maxAttempts    = "3";
    [ObservableProperty] private bool   _healthCheckEnabled;
    [ObservableProperty] private string _healthCheckUrl = string.Empty;
    [ObservableProperty] private string _healthInterval = "30";
    [ObservableProperty] private string _healthFailures = "3";

    // ── Shared: Hooks ─────────────────────────────────────────────
    [ObservableProperty] private string _freePort        = string.Empty;
    [ObservableProperty] private string _preStartCommand = string.Empty;
    [ObservableProperty] private string _postStopCommand = string.Empty;

    [ObservableProperty] private string _errorMessage = string.Empty;

    // Generated once so repeated ToScriptEntry() calls are idempotent (same Id).
    protected readonly string _id = Guid.NewGuid().ToString();

    public ObservableCollection<string> AvailableFolders { get; } = new();

    public void SetAvailableFolders(IEnumerable<string> folders)
    {
        AvailableFolders.Clear();
        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            AvailableFolders.Add(folder.Trim());
    }

    public abstract void LoadFrom(ScriptEntry entry);
    public abstract ScriptEntry? ToScriptEntry(string? existingId = null);

    /// <summary>Copies the shared fields out of an existing entry into this VM.</summary>
    protected void LoadCommonFrom(ScriptEntry entry)
    {
        Folder    = entry.Folder;
        AutoStart = entry.AutoStart;

        AutoRestart  = entry.AutoRestart;
        RestartDelay = entry.RestartDelaySeconds.ToString();
        MaxAttempts  = entry.MaxRestartAttempts.ToString();

        HealthCheckEnabled = entry.HealthCheckEnabled;
        HealthCheckUrl     = entry.HealthCheckUrl;
        HealthInterval     = entry.HealthCheckIntervalSeconds.ToString();
        HealthFailures     = entry.HealthCheckFailuresBeforeRestart.ToString();

        FreePort        = entry.FreePort > 0 ? entry.FreePort.ToString() : string.Empty;
        PreStartCommand = entry.PreStartCommand;
        PostStopCommand = entry.PostStopCommand;
    }

    /// <summary>Writes the shared fields into a freshly-built entry.</summary>
    protected void ApplyCommonTo(ScriptEntry entry)
    {
        entry.Folder    = Folder.Trim();
        entry.AutoStart = AutoStart;

        entry.AutoRestart         = AutoRestart;
        entry.RestartDelaySeconds = ParseInt(RestartDelay, 5);
        entry.MaxRestartAttempts  = ParseInt(MaxAttempts, 3);

        entry.HealthCheckEnabled               = HealthCheckEnabled;
        entry.HealthCheckUrl                   = HealthCheckUrl.Trim();
        entry.HealthCheckIntervalSeconds       = ParseInt(HealthInterval, 30);
        entry.HealthCheckFailuresBeforeRestart = ParseInt(HealthFailures, 3);

        entry.FreePort        = ParseInt(FreePort, 0);
        entry.PreStartCommand = PreStartCommand.Trim();
        entry.PostStopCommand = PostStopCommand.Trim();
    }

    protected static int ParseInt(string value, int fallback)
        => int.TryParse(value, out var n) ? Math.Max(0, n) : fallback;
}
