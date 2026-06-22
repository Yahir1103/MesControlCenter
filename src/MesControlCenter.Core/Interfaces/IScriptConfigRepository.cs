using MesControlCenter.Core.Models;

namespace MesControlCenter.Core.Interfaces;

public interface IScriptConfigRepository
{
    IReadOnlyList<ScriptEntry> Load();
    void Save(IEnumerable<ScriptEntry> entries);

    /// <summary>Folder paths ("A", "A/B") that exist even when empty.</summary>
    IReadOnlyList<string> LoadFolders();
    void SaveFolders(IEnumerable<string> folders);
}
