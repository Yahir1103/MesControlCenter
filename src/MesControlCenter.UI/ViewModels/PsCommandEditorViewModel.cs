using CommunityToolkit.Mvvm.ComponentModel;
using MesControlCenter.Core.Models;

namespace MesControlCenter.UI.ViewModels;

public partial class PsCommandEditorViewModel : EditorViewModelBase
{
    [ObservableProperty] private string _commandName = string.Empty;
    [ObservableProperty] private string _psBody      = string.Empty;
    [ObservableProperty] private bool   _runAsAdmin;

    public override void LoadFrom(ScriptEntry entry)
    {
        CommandName = entry.Name;
        PsBody      = entry.PsBody;
        RunAsAdmin  = entry.RunAsAdmin;
        LoadCommonFrom(entry);
    }

    public override ScriptEntry? ToScriptEntry(string? existingId = null)
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
        var entry = new ScriptEntry
        {
            Id         = existingId ?? _id,
            Kind       = "ps_command",
            Name       = CommandName.Trim(),
            PsBody     = StripPromptPrefixes(PsBody),
            RunAsAdmin = RunAsAdmin,
        };
        ApplyCommonTo(entry);
        return entry;
    }

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
