using System.Text.Json;
using System.Text.Json.Serialization;
using MesControlCenter.Core.Interfaces;
using MesControlCenter.Core.Models;

namespace MesControlCenter.Data;

public class JsonScriptConfigRepository : IScriptConfigRepository
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".script_control_center");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "scripts.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public IReadOnlyList<ScriptEntry> Load() => Read().Scripts.AsReadOnly();

    public void Save(IEnumerable<ScriptEntry> entries)
    {
        var w = Read();
        w.Scripts = entries.ToList();
        Write(w);
    }

    public IReadOnlyList<string> LoadFolders() => Read().Folders?.AsReadOnly() ?? (IReadOnlyList<string>)Array.Empty<string>();

    public void SaveFolders(IEnumerable<string> folders)
    {
        var w = Read();
        w.Folders = folders.ToList();
        Write(w);
    }

    private ScriptsWrapper Read()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return new ScriptsWrapper();
            return JsonSerializer.Deserialize<ScriptsWrapper>(File.ReadAllText(ConfigFile), JsonOptions)
                   ?? new ScriptsWrapper();
        }
        catch { return new ScriptsWrapper(); }
    }

    private void Write(ScriptsWrapper w)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(w, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CONFIG] [ERROR] Failed to save: {ex.Message}");
        }
    }

    private class ScriptsWrapper
    {
        public List<ScriptEntry> Scripts { get; set; } = new();
        public List<string>? Folders { get; set; }
    }
}
