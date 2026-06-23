using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MesControlCenter.Core.Models;

namespace MesControlCenter.UI.ViewModels;

public partial class PsCommandEditorViewModel : ObservableObject
{
    // ── General ───────────────────────────────────────────────────
    [ObservableProperty] private string _commandName = string.Empty;
    [ObservableProperty] private string _folder      = string.Empty;
    [ObservableProperty] private string _psBody      = string.Empty;
    [ObservableProperty] private bool   _runAsAdmin;
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

    public ObservableCollection<string> AvailableFolders { get; } = new();

    public void SetAvailableFolders(IEnumerable<string> folders)
    {
        AvailableFolders.Clear();
        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            AvailableFolders.Add(folder.Trim());
    }

    public void LoadFrom(ScriptEntry entry)
    {
        CommandName = entry.Name;
        Folder      = entry.Folder;
        PsBody      = entry.PsBody;
        RunAsAdmin  = entry.RunAsAdmin;
        AutoStart   = entry.AutoStart;

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

    public ScriptEntry? ToScriptEntry(string? existingId = null)
    {
        if (string.IsNullOrWhiteSpace(CommandName))
        {
            ErrorMessage = "Name is required.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(PsBody))
        {
            ErrorMessage = "PowerShell script body cannot be empty.";
            return null;
        }

        ErrorMessage = string.Empty;
        return new ScriptEntry
        {
            Id         = existingId ?? Guid.NewGuid().ToString(),
            Kind       = "ps_command",
            Name       = CommandName.Trim(),
            Folder     = Folder.Trim(),
            PsBody     = StripPromptPrefixes(PsBody),
            RunAsAdmin = RunAsAdmin,
            AutoStart  = AutoStart,

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

    /// <summary>
    /// Removes interactive-shell prompt prefixes that users accidentally paste:
    ///   ">> "         — PowerShell continuation prompt
    ///   "PS C:\...> " — PowerShell primary prompt
    /// </summary>
    private static string StripPromptPrefixes(string body)
    {
        var lines = body.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith(">> "))  { lines[i] = line[3..]; continue; }
            if (line.StartsWith(">>"))   { lines[i] = line[2..]; continue; }
            if (line.StartsWith("PS ") && line.Contains("> "))
            {
                var promptEnd = line.IndexOf("> ") + 2;
                lines[i] = line[promptEnd..];
            }
        }
        return string.Join(Environment.NewLine, lines);
    }
}
