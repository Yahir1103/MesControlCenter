using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MesControlCenter.Core.Models;

namespace MesControlCenter.UI.ViewModels;

public partial class NpmCommandEditorViewModel : EditorViewModelBase
{
    [ObservableProperty] private string _commandName       = string.Empty;
    [ObservableProperty] private string _npmWorkDir        = string.Empty;
    [ObservableProperty] private string _npmScript         = string.Empty;
    [ObservableProperty] private string _packageJsonStatus = string.Empty;

    public ObservableCollection<string> AvailableScripts { get; } = new();

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

    public override void LoadFrom(ScriptEntry entry)
    {
        CommandName = entry.Name;
        NpmWorkDir  = entry.WorkDir;
        NpmScript   = entry.NpmScript;
        LoadCommonFrom(entry);

        // Trigger package.json load for existing entry
        LoadPackageJsonScripts();
    }

    public override ScriptEntry? ToScriptEntry(string? existingId = null)
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
        var entry = new ScriptEntry
        {
            Id        = existingId ?? _id,
            Kind      = "npm",
            Name      = CommandName.Trim(),
            WorkDir   = NpmWorkDir,
            NpmScript = NpmScript.Trim(),
        };
        ApplyCommonTo(entry);
        return entry;
    }
}
