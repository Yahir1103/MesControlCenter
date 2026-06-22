using MesControlCenter.Core.Models;

namespace MesControlCenter.Core.Interfaces;

public interface IScriptMonitor
{
    void UpdateScripts(List<string> scripts);
    bool IsScriptRunning(string scriptName);
    ScriptStatus GetScriptStatus(string scriptName);
    IList<ScriptStatus> GetAllStatuses();
    bool AreAllScriptsActive();
}
