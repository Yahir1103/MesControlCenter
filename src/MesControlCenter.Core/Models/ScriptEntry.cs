namespace MesControlCenter.Core.Models;

public class ScriptEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>"script" | "ps_command" | "npm"</summary>
    public string Kind { get; set; } = "script";

    public string Name       { get; set; } = string.Empty;
    public string Path       { get; set; } = string.Empty;
    public string Args       { get; set; } = string.Empty;
    public string WorkDir    { get; set; } = string.Empty;
    public string Interpreter { get; set; } = string.Empty;
    public string Folder     { get; set; } = string.Empty;

    /// <summary>Multi-line PowerShell body (Kind == "ps_command")</summary>
    public string PsBody { get; set; } = string.Empty;

    /// <summary>npm script name to run — e.g. "start", "dev" (Kind == "npm")</summary>
    public string NpmScript { get; set; } = string.Empty;

    public bool RunAsAdmin { get; set; }

    // ── Auto-restart ──────────────────────────────────────────────
    public bool AutoRestart          { get; set; }
    public int  RestartDelaySeconds  { get; set; } = 5;
    /// <summary>0 = unlimited</summary>
    public int  MaxRestartAttempts   { get; set; } = 3;

    // ── HTTP Health check ─────────────────────────────────────────
    public bool   HealthCheckEnabled              { get; set; }
    public string HealthCheckUrl                  { get; set; } = string.Empty;
    public int    HealthCheckIntervalSeconds       { get; set; } = 30;
    public int    HealthCheckFailuresBeforeRestart { get; set; } = 3;

    // ── Pre / Post hooks ─────────────────────────────────────────
    public string PreStartCommand { get; set; } = string.Empty;
    public string PostStopCommand { get; set; } = string.Empty;

    /// <summary>
    /// Port to free before starting (kills whatever is listening on it).
    /// 0 = disabled. Avoids EADDRINUSE when a previous run left the port held.
    /// </summary>
    public int FreePort { get; set; }

    public bool   GitDeployEnabled        { get; set; }
    public string GitRepoDir              { get; set; } = string.Empty;
    public string GitBranch               { get; set; } = string.Empty;
    public string GitPostPullCommand      { get; set; } = string.Empty;
    public int    GitHealthTimeoutSeconds { get; set; } = 60;
    public bool   GitRollbackOnFailure    { get; set; } = true;

    public string? LastRun      { get; set; }
    public int?    LastExitCode { get; set; }
    public bool    AutoStart    { get; set; }

    public bool IsExecutable => Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    public bool IsBatchFile  => Path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                             || Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
    public bool IsPsFile     => Path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
    public bool IsPsCommand  => Kind == "ps_command";
    public bool IsNpmCommand => Kind == "npm";
}
