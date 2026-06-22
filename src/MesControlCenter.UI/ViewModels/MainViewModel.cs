using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MesControlCenter.Core.Interfaces;
using MesControlCenter.Core.Models;
using MesControlCenter.Core.Services;
using MesControlCenter.UI.Helpers;
using MesControlCenter.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlCenter.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IScriptConfigRepository _configRepo;
    private readonly GitDeployService _gitDeployService;
    private readonly ResourceMonitorService _resourceMonitorService;
    private readonly LocalBackupService _backupService;

    // Script entries and processes
    private readonly Dictionary<string, ScriptEntry>    _entries    = new();
    private readonly Dictionary<string, Process>        _processes  = new();
    // Job object per running entry: disposing it kills the whole process tree
    // (cmd → npm → node) reliably, which taskkill /T does not for npm.
    private readonly Dictionary<string, ProcessJob>     _jobs       = new();
    private readonly Dictionary<string, StringBuilder>  _logBuffers = new();
    private readonly Dictionary<string, int>            _logLineCount = new();
    private readonly Dictionary<string, ScriptEntryViewModel> _scriptVmsById = new();
    private readonly ConcurrentQueue<QueuedLogMessage> _queuedLogMessages = new();
    private readonly HashSet<string> _dirtyLogEntries = new();

    // Folder tree: explicit folder paths (incl. empty ones) + the built tree.
    private readonly SortedSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<object> FolderTree { get; } = new();

    // Auto-restart
    private readonly Dictionary<string, int>  _restartAttempts = new();
    private readonly HashSet<string>          _manuallyStopped = new();
    private readonly HashSet<string>          _deployingEntries = new();

    // Health check
    private static readonly HttpClient                       _httpClient   = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<string, DispatcherTimer>     _healthTimers    = new();
    private readonly Dictionary<string, int>                 _healthFailures  = new();

    private const int MaxLinesInMemory   = 1000;
    private const int KeepLinesAfterFlush = 200;
    private const int MaxQueuedLogMessagesPerTick = 1000;
    private const int MaxDisplayedProcessRows = 6;
    private static readonly TimeSpan LogUiRefreshInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ConfigSaveDebounceInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ResourceMonitorInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TemperatureMonitorInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DatabaseMonitorInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TemperatureReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex AnsiEscapeRegex = new(
        "\u001B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled);
    private readonly record struct QueuedLogMessage(string EntryId, string Text);

    // Timers
    private DispatcherTimer? _autoStartTimer;
    private DispatcherTimer? _logFlushTimer;
    private DispatcherTimer? _logUiTimer;
    private DispatcherTimer? _configSaveTimer;
    private DispatcherTimer? _resourceMonitorTimer;
    private DispatcherTimer? _temperatureMonitorTimer;
    private DispatcherTimer? _databaseMonitorTimer;
    private bool _configSavePending;
    private bool _resourceRefreshInProgress;
    private bool _databaseRefreshInProgress;
    private Task<(double? ValueC, string Source)>? _temperatureReadTask;

    private CancellationTokenSource? _gitCommitCts;

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".script_control_center", "logs");

    public MainViewModel(
        IScriptConfigRepository configRepo,
        GitDeployService gitDeployService,
        ResourceMonitorService resourceMonitorService,
        LocalBackupService backupService)
    {
        _configRepo = configRepo;
        _gitDeployService = gitDeployService;
        _resourceMonitorService = resourceMonitorService;
        _backupService = backupService;

        Scripts = new ObservableCollection<ScriptEntryViewModel>();
        FilteredScripts = CollectionViewSource.GetDefaultView(Scripts);
        FilteredScripts.Filter = FilterScript;
        if (FilteredScripts.GroupDescriptions is { } groups)
            groups.Add(new PropertyGroupDescription(nameof(ScriptEntryViewModel.FolderGroupKey)));

        _logUiTimer = new DispatcherTimer { Interval = LogUiRefreshInterval };
        _logUiTimer.Tick += (_, _) => DrainQueuedLogsAndRefreshView();
        _logUiTimer.Start();

        _configSaveTimer = new DispatcherTimer { Interval = ConfigSaveDebounceInterval };
        _configSaveTimer.Tick += (_, _) =>
        {
            _configSaveTimer.Stop();
            FlushPendingConfigSave();
        };

        Directory.CreateDirectory(LogDir);
    }

    // ═══════ Observable Properties ═══════

    public ObservableCollection<ScriptEntryViewModel> Scripts { get; }
    public ICollectionView FilteredScripts { get; }

    [ObservableProperty]
    private ScriptEntryViewModel? _selectedScript;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _logContent = string.Empty;

    [ObservableProperty]
    private string _globalStatusText = "No scripts";

    [ObservableProperty]
    private SolidColorBrush _globalStatusColor = new((Color)ColorConverter.ConvertFromString("#4a5568"));

    [ObservableProperty]
    private string _globalStatusImageUri = "pack://application:,,,/Resources/DESCONECTADO.png";

    [ObservableProperty]
    private string _pcTemperatureText = "N/A";

    [ObservableProperty]
    private string _pcTemperatureSourceText = "No sensor";

    [ObservableProperty]
    private string _resourceMonitorStatusText = "No running processes";

    [ObservableProperty]
    private bool _hasRunningProcessUsage;

    // System-wide metrics (toolbar)
    [ObservableProperty] private string _sysCpuText = "—";
    [ObservableProperty] private string _sysRamText = "—";
    [ObservableProperty] private string _sysRamDetail = string.Empty;
    [ObservableProperty] private string _sysTempText = "—";
    [ObservableProperty] private string _dbStatusText = "DB";
    [ObservableProperty] private SolidColorBrush _dbStatusColor = new((Color)ColorConverter.ConvertFromString("#7d8590"));
    [ObservableProperty] private string _dbStatusToolTip = "Database status not checked yet.";

    // Selected-script usage (detail panel)
    [ObservableProperty] private string _procUsagePid = string.Empty;
    [ObservableProperty] private string _procUsageName = string.Empty;
    [ObservableProperty] private string _procUsageCpu = string.Empty;
    [ObservableProperty] private string _procUsageRam = string.Empty;

    partial void OnSelectedScriptChanged(ScriptEntryViewModel? value)
    {
        LoadLogIntoView(value?.Id);
        UpdateDeployingState();
        RefreshSelectedGitCommit(value);
        _ = RefreshResourceMonitorAsync(); // show the new selection's usage now
    }

    partial void OnSearchTextChanged(string value)
    {
        FilteredScripts.Refresh();
    }

    // ═══════ Initialization ═══════

    public void Initialize()
    {
        LoadConfig();
        UpdateGlobalStatus();

        // Auto-start after 1 second
        _autoStartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoStartTimer.Tick += (_, _) =>
        {
            _autoStartTimer.Stop();
            AutoStartScripts();
        };
        _autoStartTimer.Start();

        // Flush logs to disk and clear buffers every hour to prevent UI lag
        _logFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _logFlushTimer.Tick += (_, _) => FlushAndClearLogs();
        _logFlushTimer.Start();

        _resourceMonitorTimer = new DispatcherTimer { Interval = ResourceMonitorInterval };
        _resourceMonitorTimer.Tick += async (_, _) => await RefreshResourceMonitorAsync();
        _resourceMonitorTimer.Start();
        _ = RefreshResourceMonitorAsync();

        _temperatureMonitorTimer = new DispatcherTimer { Interval = TemperatureMonitorInterval };
        _temperatureMonitorTimer.Tick += async (_, _) => await RefreshTemperatureAsync();
        _temperatureMonitorTimer.Start();
        _ = RefreshTemperatureAsync();

        _databaseMonitorTimer = new DispatcherTimer { Interval = DatabaseMonitorInterval };
        _databaseMonitorTimer.Tick += async (_, _) => await RefreshDatabaseStatusAsync();
        _databaseMonitorTimer.Start();
        _ = RefreshDatabaseStatusAsync();
    }

    // ═══════ Config I/O ═══════

    private void LoadConfig()
    {
        _entries.Clear();
        _scriptVmsById.Clear();
        Scripts.Clear();
        _folders.Clear();

        _bulkLoading = true;
        var loaded = _configRepo.Load();
        foreach (var entry in loaded)
        {
            _entries[entry.Id] = entry;
            AddScriptVm(entry);
        }
        _bulkLoading = false;

        foreach (var f in _configRepo.LoadFolders())
            AddFolderPath(f);
        // Also register folders implied by scripts' Folder paths.
        foreach (var e in _entries.Values)
            AddFolderPath(e.Folder);

        RebuildTree();
    }

    // ═══════ Folder tree ═══════

    private static string NormalizeFolder(string? path)
        => string.Join('/', (path ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // Registers a folder path and every ancestor ("A/B/C" → A, A/B, A/B/C).
    private void AddFolderPath(string? path)
    {
        var norm = NormalizeFolder(path);
        if (norm.Length == 0) return;
        var parts = norm.Split('/');
        for (int i = 1; i <= parts.Length; i++)
            _folders.Add(string.Join('/', parts[..i]));
    }

    private void PersistFolders() => _configRepo.SaveFolders(_folders);

    /// <summary>Rebuilds FolderTree from _folders + each script's Folder path.</summary>
    private void RebuildTree()
    {
        var nodes = new Dictionary<string, FolderNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        FolderNodeViewModel NodeFor(string path)
        {
            if (nodes.TryGetValue(path, out var existing)) return existing;
            var node = new FolderNodeViewModel(path);
            nodes[path] = node;
            var slash = path.LastIndexOf('/');
            if (slash < 0)
                _rootBuffer.Add(node);
            else
                NodeFor(path[..slash]).Children.Add(node);
            return node;
        }

        _rootBuffer = new List<object>();
        // Build folder nodes first so empty folders show up.
        foreach (var path in _folders)
            NodeFor(path);

        // Place scripts in their folder (or root if uncategorized).
        var uncategorized = new List<object>();
        foreach (var entry in _entries.Values)
        {
            var vm = GetScriptVm(entry.Id);
            if (vm == null) continue;
            var folder = NormalizeFolder(entry.Folder);
            if (folder.Length == 0) uncategorized.Add(vm);
            else NodeFor(folder).Children.Add(vm);
        }

        // Sort children: folders first (by name), then scripts (by name).
        foreach (var node in nodes.Values)
            SortChildren(node.Children);
        SortChildren(_rootBuffer);

        FolderTree.Clear();
        foreach (var item in _rootBuffer) FolderTree.Add(item);
        foreach (var s in uncategorized.OrderBy(o => ((ScriptEntryViewModel)o).Name, StringComparer.OrdinalIgnoreCase))
            FolderTree.Add(s);
    }

    private List<object> _rootBuffer = new();

    private static void SortChildren(IList<object> items)
    {
        var sorted = items
            .OrderBy(o => o is FolderNodeViewModel ? 0 : 1)
            .ThenBy(o => o is FolderNodeViewModel f ? f.Name : ((ScriptEntryViewModel)o).Name,
                    StringComparer.OrdinalIgnoreCase)
            .ToList();
        items.Clear();
        foreach (var o in sorted) items.Add(o);
    }

    // ── Folder operations (called from the tree's context menu / drag-drop) ──

    /// <summary>Creates a sub-folder under parentPath (empty = root). Returns its path.</summary>
    public string CreateFolder(string? parentPath, string name)
    {
        var clean = NormalizeFolder(name);
        if (clean.Length == 0) return string.Empty;
        var parent = NormalizeFolder(parentPath);
        var path = parent.Length == 0 ? clean : $"{parent}/{clean}";
        AddFolderPath(path);
        PersistFolders();
        RebuildTree();
        ExpandTo(path);
        return path;
    }

    public void RenameFolder(string folderPath, string newName)
    {
        var path = NormalizeFolder(folderPath);
        var clean = NormalizeFolder(newName);
        if (path.Length == 0 || clean.Length == 0) return;

        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? "" : path[..slash];
        var newPath = parent.Length == 0 ? clean : $"{parent}/{clean}";
        if (newPath.Equals(path, StringComparison.OrdinalIgnoreCase)) return;

        ReparentPrefix(path, newPath);
    }

    /// <summary>Deletes a folder. Its scripts and sub-folders move up to the parent.</summary>
    public void DeleteFolder(string folderPath)
    {
        var path = NormalizeFolder(folderPath);
        if (path.Length == 0) return;
        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? "" : path[..slash];

        // Reassign scripts whose folder is this path (or under it) up one level.
        foreach (var e in _entries.Values)
        {
            var f = NormalizeFolder(e.Folder);
            if (f.Equals(path, StringComparison.OrdinalIgnoreCase))
                e.Folder = parent;
            else if (f.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
                e.Folder = parent + f[path.Length..]; // keep the deeper tail, drop this segment? -> move up
        }

        // Remove this folder and its descendants from the explicit set.
        _folders.RemoveWhere(f =>
            f.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            f.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

        SaveConfig();
        PersistFolders();
        RebuildTree();
    }

    /// <summary>Moves a script to a target folder (empty = root / uncategorized).</summary>
    public void MoveScriptToFolder(string entryId, string? targetFolderPath)
    {
        if (!_entries.TryGetValue(entryId, out var entry)) return;
        var target = NormalizeFolder(targetFolderPath);
        if (NormalizeFolder(entry.Folder).Equals(target, StringComparison.OrdinalIgnoreCase)) return;
        entry.Folder = target;
        SaveConfig();
        RebuildTree();
        if (target.Length > 0) ExpandTo(target);
    }

    /// <summary>Moves a folder (and its subtree) under a new parent (empty = root).</summary>
    public void MoveFolder(string folderPath, string? newParentPath)
    {
        var path = NormalizeFolder(folderPath);
        var newParent = NormalizeFolder(newParentPath);
        if (path.Length == 0) return;
        // Can't move into itself or a descendant.
        if (newParent.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            newParent.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) return;

        var name = path[(path.LastIndexOf('/') + 1)..];
        var newPath = newParent.Length == 0 ? name : $"{newParent}/{name}";
        if (newPath.Equals(path, StringComparison.OrdinalIgnoreCase)) return;
        ReparentPrefix(path, newPath);
    }

    // Rewrites every folder/script path that starts with oldPrefix to newPrefix.
    private void ReparentPrefix(string oldPrefix, string newPrefix)
    {
        foreach (var e in _entries.Values)
        {
            var f = NormalizeFolder(e.Folder);
            if (f.Equals(oldPrefix, StringComparison.OrdinalIgnoreCase))
                e.Folder = newPrefix;
            else if (f.StartsWith(oldPrefix + "/", StringComparison.OrdinalIgnoreCase))
                e.Folder = newPrefix + f[oldPrefix.Length..];
        }

        var updated = _folders
            .Select(f => f.Equals(oldPrefix, StringComparison.OrdinalIgnoreCase) ? newPrefix
                       : f.StartsWith(oldPrefix + "/", StringComparison.OrdinalIgnoreCase) ? newPrefix + f[oldPrefix.Length..]
                       : f)
            .ToList();
        _folders.Clear();
        foreach (var f in updated) AddFolderPath(f);

        SaveConfig();
        PersistFolders();
        RebuildTree();
        ExpandTo(newPrefix);
    }

    private void ExpandTo(string path)
    {
        // Expand all ancestor nodes so the target is visible.
        FolderNodeViewModel? Find(IEnumerable<object> items, string p)
        {
            foreach (var f in items.OfType<FolderNodeViewModel>())
            {
                if (f.Path.Equals(p, StringComparison.OrdinalIgnoreCase)) return f;
                var hit = Find(f.Children, p);
                if (hit != null) { f.IsExpanded = true; return hit; }
            }
            return null;
        }
        Find(FolderTree, NormalizeFolder(path));
    }

    private void SaveConfig()
    {
        _configSavePending = false;
        _configSaveTimer?.Stop();
        _configRepo.Save(_entries.Values);
    }

    private void RequestConfigSave()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(RequestConfigSave), DispatcherPriority.Background);
            return;
        }

        _configSavePending = true;
        _configSaveTimer?.Stop();
        _configSaveTimer?.Start();
    }

    private void FlushPendingConfigSave()
    {
        if (!_configSavePending) return;
        _configSavePending = false;
        _configRepo.Save(_entries.Values);
    }

    private bool _bulkLoading;

    private ScriptEntryViewModel AddScriptVm(ScriptEntry entry)
    {
        var vm = new ScriptEntryViewModel(entry);
        Scripts.Add(vm);
        _scriptVmsById[entry.Id] = vm;
        if (!_bulkLoading)
        {
            AddFolderPath(entry.Folder);
            RebuildTree();
        }
        return vm;
    }

    private ScriptEntryViewModel? GetScriptVm(string entryId)
        => _scriptVmsById.TryGetValue(entryId, out var vm) ? vm : null;

    // Tracks a started process and wraps it in a Job Object so stopping kills
    // the whole tree (cmd → npm → node) reliably.
    private void RegisterProcess(string entryId, Process proc)
    {
        _processes[entryId] = proc;
        try
        {
            var job = new ProcessJob();
            if (job.AssignProcess(proc))
                _jobs[entryId] = job;
            else
                job.Dispose();
        }
        catch { /* job is best-effort; taskkill fallback still runs on stop */ }
    }

    private IReadOnlyList<string> GetAvailableFolders()
    {
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ALMACEN",
            "PROD",
            "EMBARQUES",
            "SHARED"
        };

        foreach (var folder in _entries.Values.Select(e => e.Folder).Where(f => !string.IsNullOrWhiteSpace(f)))
            folders.Add(folder.Trim());

        return folders.ToList();
    }

    // ═══════ Filtering ═══════

    private bool FilterScript(object obj)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        if (obj is not ScriptEntryViewModel vm) return false;
        var q = SearchText.Trim();
        return vm.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.Path.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.FolderName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.FolderPath.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════ Log ═══════

    private void AppendLog(string entryId, string text)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            QueueLog(entryId, text);
            return;
        }

        if (!_logBuffers.ContainsKey(entryId))
        {
            _logBuffers[entryId] = new StringBuilder();
            _logLineCount[entryId] = 0;
        }

        var ts = DateTime.Now.ToString("HH:mm:ss");
        var normalizedText = NormalizeLogText(text);
        var lines = normalizedText.Split('\n', StringSplitOptions.None);
        foreach (var line in lines)
        {
            var cleanLine = line.TrimEnd();
            if (!string.IsNullOrWhiteSpace(cleanLine))
            {
                var stamped = $"[{ts}] {cleanLine}";
                _logBuffers[entryId].AppendLine(stamped);
                _logLineCount[entryId]++;
            }
        }

        // Auto-flush to disk when buffer gets too large
        if (_logLineCount[entryId] >= MaxLinesInMemory)
        {
            FlushEntryToDisk(entryId);
        }

        _dirtyLogEntries.Add(entryId);
    }

    private void QueueLog(string entryId, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _queuedLogMessages.Enqueue(new QueuedLogMessage(entryId, text));
    }

    private void DrainQueuedLogsAndRefreshView(int maxMessages = MaxQueuedLogMessagesPerTick)
    {
        var drained = 0;
        while (drained < maxMessages && _queuedLogMessages.TryDequeue(out var message))
        {
            AppendLog(message.EntryId, message.Text);
            drained++;
        }

        if (SelectedScript != null
            && _dirtyLogEntries.Contains(SelectedScript.Id)
            && _logBuffers.TryGetValue(SelectedScript.Id, out var buffer))
        {
            LogContent = TailLines(buffer.ToString(), LogViewTailLines);
        }

        if (_dirtyLogEntries.Count > 0)
            _dirtyLogEntries.Clear();
    }

    private static string NormalizeLogText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var withoutAnsi = AnsiEscapeRegex.Replace(text, string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        var clean = new StringBuilder(withoutAnsi.Length);
        foreach (var ch in withoutAnsi)
        {
            if (ch == '\b')
            {
                if (clean.Length > 0 && clean[^1] != '\n')
                    clean.Length--;
                continue;
            }

            if (ch == '\n' || ch == '\t' || !char.IsControl(ch))
                clean.Append(ch);
        }

        return clean.ToString();
    }

    private static void ConfigurePlainTextOutput(ProcessStartInfo psi)
    {
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["FORCE_COLOR"] = "0";
        psi.Environment["CLICOLOR"] = "0";
        psi.Environment["npm_config_color"] = "false";
        psi.Environment["npm_config_progress"] = "false";
    }

    private void LoadLogIntoView(string? entryId)
    {
        if (entryId == null || !_logBuffers.TryGetValue(entryId, out var buffer))
        {
            LogContent = string.Empty;
            return;
        }
        LogContent = TailLines(buffer.ToString(), LogViewTailLines);
    }

    // ponytail: a NoWrap TextBox re-measures the widest line over its WHOLE text
    // on every reassignment. Streaming output reassigned the full ~1000-line
    // buffer every 100ms → 100% CPU stuck in WPF MeasureCore. Show only the tail;
    // full history still goes to disk. Bump if you need more visible scrollback.
    private const int LogViewTailLines = 400;

    private static string TailLines(string text, int maxLines)
    {
        if (string.IsNullOrEmpty(text)) return text;
        int idx = text.Length, count = 0;
        while (idx > 0 && count <= maxLines)
        {
            int nl = text.LastIndexOf('\n', idx - 1);
            if (nl < 0) return text;
            count++;
            idx = nl;
        }
        return text[(idx + 1)..];
    }

    /// <summary>
    /// Flushes a single entry's log buffer to disk, keeping only the last N lines in memory.
    /// </summary>
    private void FlushEntryToDisk(string entryId)
    {
        if (!_logBuffers.TryGetValue(entryId, out var buffer) || buffer.Length == 0) return;

        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var timestamp = DateTime.Now.ToString("HH:mm:ss");

        var name = _entries.TryGetValue(entryId, out var entry) ? entry.Name : entryId;
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        var logFile = Path.Combine(LogDir, $"{safeName}_{date}.log");

        var fullText = buffer.ToString();

        try
        {
            using var writer = new StreamWriter(logFile, append: true, encoding: Encoding.UTF8);
            writer.WriteLine($"\n--- Flush at {timestamp} ---");
            writer.Write(fullText);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LOG] Error writing log file for {name}: {ex.Message}");
            return; // Don't clear buffer if write failed
        }

        // Keep only the last N lines in memory
        var allLines = fullText.Split('\n', StringSplitOptions.None);
        buffer.Clear();
        var keepFrom = Math.Max(0, allLines.Length - KeepLinesAfterFlush);
        for (int i = keepFrom; i < allLines.Length; i++)
        {
            if (!string.IsNullOrEmpty(allLines[i]))
                buffer.AppendLine(allLines[i]);
        }
        _logLineCount[entryId] = Math.Min(allLines.Length, KeepLinesAfterFlush);
    }

    /// <summary>
    /// Writes all in-memory log buffers to daily log files, then clears the buffers.
    /// One file per script per day: {ScriptName}_{yyyy-MM-dd}.log
    /// </summary>
    private void FlushAndClearLogs()
    {
        foreach (var entryId in _logBuffers.Keys.ToList())
        {
            FlushEntryToDisk(entryId);
        }

        // Refresh the current view
        if (SelectedScript != null)
            LogContent = _logBuffers.TryGetValue(SelectedScript.Id, out var sb) ? sb.ToString() : string.Empty;
    }

    // ═══════ Resource Monitor ═══════

    private async Task RefreshResourceMonitorAsync()
    {
        if (_resourceRefreshInProgress)
            return;

        _resourceRefreshInProgress = true;
        try
        {
            var runningScripts = GetRunningScriptProcesses();
            var snapshot = await Task.Run(() => _resourceMonitorService.Capture(runningScripts));
            ApplyResourceSnapshot(snapshot);
        }
        catch
        {
            ResourceMonitorStatusText = "Resource monitor unavailable";
        }
        finally
        {
            _resourceRefreshInProgress = false;
        }
    }

    private async Task RefreshTemperatureAsync()
    {
        if (_temperatureReadTask is { IsCompleted: false })
            return;

        try
        {
            _temperatureReadTask = Task.Run(() => _resourceMonitorService.ReadTemperatureSnapshot());
            var completedTask = await Task.WhenAny(_temperatureReadTask, Task.Delay(TemperatureReadTimeout));

            if (!ReferenceEquals(completedTask, _temperatureReadTask))
            {
                PcTemperatureSourceText = "Sensor loading";
                return;
            }

            var reading = await _temperatureReadTask;
            PcTemperatureText = reading.ValueC.HasValue ? $"{reading.ValueC.Value:0.0} C" : "N/A";
            PcTemperatureSourceText = reading.Source;
        }
        catch
        {
            PcTemperatureText = "N/A";
            PcTemperatureSourceText = "Sensor unavailable";
        }
    }

    private async Task RefreshDatabaseStatusAsync()
    {
        if (_databaseRefreshInProgress)
            return;

        _databaseRefreshInProgress = true;
        try
        {
            var status = await _backupService.CheckDatabaseHealthAsync(TimeSpan.FromSeconds(4));
            var checkedAt = status.CheckedAt.ToString("HH:mm:ss");

            if (!status.IsConfigured)
            {
                DbStatusColor = Brush("#7d8590");
                DbStatusToolTip = $"DB not configured. Last check: {checkedAt}";
                return;
            }

            DbStatusColor = status.IsOnline ? Brush("#3fb950") : Brush("#f85149");
            DbStatusToolTip = $"{status.Message} Last check: {checkedAt}";
        }
        catch (Exception ex)
        {
            DbStatusColor = Brush("#f85149");
            DbStatusToolTip = $"Database check failed: {ex.Message}";
        }
        finally
        {
            _databaseRefreshInProgress = false;
        }
    }

    // Only the currently selected script (if it's running). The user wants the
    // usage of the one they picked, not a table of everything.
    private IReadOnlyCollection<RunningScriptProcess> GetRunningScriptProcesses()
    {
        var id = SelectedScript?.Id;
        if (id == null
            || !_processes.TryGetValue(id, out var process)
            || !_entries.TryGetValue(id, out var entry))
            return Array.Empty<RunningScriptProcess>();

        try
        {
            if (process.HasExited)
                return Array.Empty<RunningScriptProcess>();
            return new[] { new RunningScriptProcess(entry.Id, entry.Name, process.Id) };
        }
        catch
        {
            return Array.Empty<RunningScriptProcess>();
        }
    }

    // ponytail: flat properties, no ItemsControl/nested Grids (those recursed in
    // WPF MeasureCore and pinned the UI at 100% CPU). System metrics live in the
    // toolbar; the per-process block shows the selected script only.
    private void ApplyResourceSnapshot(ResourceMonitorSnapshot snapshot)
    {
        // System-wide metrics (always shown, top toolbar).
        SysCpuText = $"{snapshot.SystemCpuPercent:0}%";
        SysRamText = $"{snapshot.SystemRamPercent:0}%";
        SysRamDetail = $"{snapshot.SystemRamUsedMb / 1024d:0.0}/{snapshot.SystemRamTotalMb / 1024d:0.0} GB";
        SysTempText = snapshot.TemperatureC.HasValue ? $"{snapshot.TemperatureC.Value:0.0}°C" : "—";

        // Selected script usage.
        var u = snapshot.Processes.FirstOrDefault();
        HasRunningProcessUsage = u != null;
        if (u == null)
        {
            ProcUsagePid = ProcUsageName = ProcUsageCpu = ProcUsageRam = string.Empty;
            return;
        }
        ProcUsagePid = u.ProcessId.ToString();
        ProcUsageName = u.ProcessName;
        ProcUsageCpu = $"{u.CpuPercent:0.0}%";
        ProcUsageRam = u.MemoryMb >= 1024 ? $"{u.MemoryMb / 1024d:0.0} GB" : $"{u.MemoryMb:0} MB";
    }

    private static SolidColorBrush Brush(string color)
        => new((Color)ColorConverter.ConvertFromString(color));

    // ═══════ Commands ═══════

    [RelayCommand]
    private void AddScript()
    {
        var editorWindow = new ScriptEditorWindow(availableFolders: GetAvailableFolders());
        editorWindow.Owner = Application.Current.MainWindow;
        if (editorWindow.ShowDialog() == true && editorWindow.DataContext is ScriptEditorViewModel editorVm)
        {
            var entry = editorVm.ToScriptEntry();
            if (entry == null) return;
            _entries[entry.Id] = entry;
            SaveConfig();
            AddScriptVm(entry);
        }
    }

    [RelayCommand]
    private void AddPsCommand()
    {
        var editorWindow = new PsCommandEditorWindow(availableFolders: GetAvailableFolders());
        editorWindow.Owner = Application.Current.MainWindow;
        if (editorWindow.ShowDialog() == true && editorWindow.DataContext is PsCommandEditorViewModel editorVm)
        {
            var entry = editorVm.ToScriptEntry();
            if (entry == null) return;
            _entries[entry.Id] = entry;
            SaveConfig();
            AddScriptVm(entry);
        }
    }

    [RelayCommand]
    private void AddNpmCommand()
    {
        var editorWindow = new NpmCommandEditorWindow(availableFolders: GetAvailableFolders());
        editorWindow.Owner = Application.Current.MainWindow;
        if (editorWindow.ShowDialog() == true && editorWindow.DataContext is NpmCommandEditorViewModel editorVm)
        {
            var entry = editorVm.ToScriptEntry();
            if (entry == null) return;
            _entries[entry.Id] = entry;
            SaveConfig();
            AddScriptVm(entry);
        }
    }

    [RelayCommand]
    private void EditScript()
    {
        if (SelectedScript == null) return;
        if (IsEntryDeploying(SelectedScript.Id))
        {
            MessageBox.Show("Wait for the deploy to finish before editing this script.", "Deploy in progress",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var entry = SelectedScript.Entry;

        if (entry.IsPsCommand)
        {
            var psWindow = new PsCommandEditorWindow(entry, GetAvailableFolders());
            psWindow.Owner = Application.Current.MainWindow;
            if (psWindow.ShowDialog() == true && psWindow.DataContext is PsCommandEditorViewModel psVm)
            {
                var updated = psVm.ToScriptEntry(entry.Id);
                if (updated == null) return;
                _entries[entry.Id] = updated;
                SaveConfig();
                RefreshScriptVm(SelectedScript, updated);
            }
            return;
        }

        if (entry.IsNpmCommand)
        {
            var npmWindow = new NpmCommandEditorWindow(entry, GetAvailableFolders());
            npmWindow.Owner = Application.Current.MainWindow;
            if (npmWindow.ShowDialog() == true && npmWindow.DataContext is NpmCommandEditorViewModel npmVm)
            {
                var updated = npmVm.ToScriptEntry(entry.Id);
                if (updated == null) return;
                _entries[entry.Id] = updated;
                SaveConfig();
                RefreshScriptVm(SelectedScript, updated);
            }
            return;
        }

        var editorWindow = new ScriptEditorWindow(entry, GetAvailableFolders());
        editorWindow.Owner = Application.Current.MainWindow;
        if (editorWindow.ShowDialog() == true && editorWindow.DataContext is ScriptEditorViewModel editorVm)
        {
            var updated = editorVm.ToScriptEntry(entry.Id);
            if (updated == null) return;
            _entries[entry.Id] = updated;
            SaveConfig();
            RefreshScriptVm(SelectedScript, updated);
        }
    }

    private void RefreshScriptVm(ScriptEntryViewModel old, ScriptEntry updated)
    {
        var idx = Scripts.IndexOf(old);
        if (idx >= 0)
        {
            var vm = new ScriptEntryViewModel(updated)
            {
                IsDeploying = _deployingEntries.Contains(updated.Id)
            };
            Scripts[idx] = vm;
            _scriptVmsById[updated.Id] = vm;
            AddFolderPath(updated.Folder);
            RebuildTree();
            SelectedScript = vm;
        }
    }

    [RelayCommand]
    private void RemoveScript()
    {
        if (SelectedScript == null) return;
        var eid = SelectedScript.Id;

        if (IsEntryDeploying(eid))
        {
            MessageBox.Show("Wait for the deploy to finish before removing this script.", "Deploy in progress",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_processes.ContainsKey(eid))
        {
            MessageBox.Show("Stop the script before removing it.", "Running", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show("Remove selected script?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        _entries.Remove(eid);
        _scriptVmsById.Remove(eid);
        Scripts.Remove(SelectedScript);
        SaveConfig();
        RebuildTree();
        LogContent = string.Empty;
    }

    [RelayCommand]
    private void RunScript()
    {
        if (SelectedScript != null)
            RunEntry(SelectedScript.Id);
    }

    [RelayCommand]
    private void StopScript()
    {
        if (SelectedScript != null)
            StopEntry(SelectedScript.Id);
    }

    [RelayCommand]
    private async Task DeployGit()
    {
        if (SelectedScript == null) return;

        var entryId = SelectedScript.Id;
        if (!_entries.TryGetValue(entryId, out var entry)) return;
        if (!SelectedScript.CanGitDeploy) return;

        if (IsEntryDeploying(entryId))
            return;

        SetEntryDeploying(entryId, true);

        try
        {
            AppendDeployLog(entryId, $"Starting deploy for {entry.Name}");

            var result = await _gitDeployService.DeployAsync(new GitDeployRequest
            {
                Entry = entry,
                Log = message => AppendDeployLog(entryId, message),
                StopScriptAsync = () => StopEntryForDeployAsync(entryId),
                StartScriptAsync = () => StartEntryForDeployAsync(entryId),
                WaitForHealthyAsync = () => WaitForDeployValidationAsync(entryId, entry)
            });

            AppendDeployLog(entryId, result.Message);

            if (!result.Succeeded)
            {
                MessageBox.Show(result.Message, "Deploy Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            AppendDeployLog(entryId, $"Unexpected deploy error: {ex.Message}");
            MessageBox.Show(ex.Message, "Deploy Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetEntryDeploying(entryId, false);
            if (SelectedScript?.Id == entryId)
                RefreshSelectedGitCommit(SelectedScript);
        }
    }

    [RelayCommand]
    private void RunAll()
    {
        foreach (var eid in _entries.Keys.ToList())
        {
            if (!IsEntryDeploying(eid))
                RunEntry(eid);
        }
    }

    [RelayCommand]
    private void StopAll()
    {
        foreach (var eid in _processes.Keys.ToList())
        {
            if (!IsEntryDeploying(eid))
                StopEntry(eid);
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogContent = string.Empty;
        if (SelectedScript != null && _logBuffers.ContainsKey(SelectedScript.Id))
        {
            _logBuffers[SelectedScript.Id].Clear();
            _logLineCount[SelectedScript.Id] = 0;
        }
    }

    [RelayCommand]
    private void SaveLog()
    {
        if (SelectedScript == null) return;
        var eid = SelectedScript.Id;
        if (!_logBuffers.TryGetValue(eid, out var buffer)) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = LogDir,
            FileName = $"{SelectedScript.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.log",
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                File.WriteAllText(dlg.FileName, buffer.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save log: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    [RelayCommand]
    private void CopyLog()
    {
        if (!string.IsNullOrEmpty(LogContent))
            Clipboard.SetText(LogContent);
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = LogDir, UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void OpenBackups()
    {
        try
        {
            var backupVm = App.Services.GetRequiredService<BackupViewModel>();
            var backupWindow = new BackupWindow
            {
                Owner = Application.Current.MainWindow,
                DataContext = backupVm
            };
            backupWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open backups: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ═══════ Run / Stop Logic ═══════

    private bool IsEntryDeploying(string entryId) => _deployingEntries.Contains(entryId);

    private void SetEntryDeploying(string entryId, bool isDeploying)
    {
        if (isDeploying)
            _deployingEntries.Add(entryId);
        else
            _deployingEntries.Remove(entryId);

        UpdateDeployingState();
    }

    private void UpdateDeployingState()
    {
        foreach (var vm in Scripts)
            vm.IsDeploying = _deployingEntries.Contains(vm.Id);
    }

    private void RefreshSelectedGitCommit(ScriptEntryViewModel? vm)
    {
        _gitCommitCts?.Cancel();
        _gitCommitCts = null;

        if (vm == null)
            return;

        vm.GitCommitText = string.Empty;
        vm.IsGitCommitLoading = false;

        if (!vm.CanGitDeploy)
            return;

        var cts = new CancellationTokenSource();
        _gitCommitCts = cts;
        vm.IsGitCommitLoading = true;
        vm.GitCommitText = "Loading commit...";

        _ = LoadGitCommitAsync(vm, cts);
    }

    private async Task LoadGitCommitAsync(ScriptEntryViewModel vm, CancellationTokenSource cts)
    {
        try
        {
            var commitText = await _gitDeployService.GetCurrentCommitSummaryAsync(vm.Entry, cts.Token);
            if (cts.Token.IsCancellationRequested
                || SelectedScript?.Id != vm.Id
                || !ReferenceEquals(_gitCommitCts, cts))
                return;

            vm.GitCommitText = string.IsNullOrWhiteSpace(commitText)
                ? "Commit unavailable"
                : commitText;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.Token.IsCancellationRequested
                && SelectedScript?.Id == vm.Id
                && ReferenceEquals(_gitCommitCts, cts))
                vm.GitCommitText = $"Commit unavailable: {ShortenForDisplay(ex.Message, 90)}";
        }
        finally
        {
            if (ReferenceEquals(_gitCommitCts, cts) && SelectedScript?.Id == vm.Id)
                vm.IsGitCommitLoading = false;

            if (ReferenceEquals(_gitCommitCts, cts))
                _gitCommitCts = null;

            cts.Dispose();
        }
    }

    private static string ShortenForDisplay(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        return value[..maxLength].TrimEnd() + "...";
    }

    private void AppendDeployLog(string entryId, string message)
    {
        if (Application.Current.Dispatcher.CheckAccess())
            AppendLog(entryId, $"[DEPLOY] {message}");
        else
            QueueLog(entryId, $"[DEPLOY] {message}");
    }

    private async Task<bool> StopEntryForDeployAsync(string entryId)
    {
        if (!_processes.TryGetValue(entryId, out var proc))
            return true;

        await Application.Current.Dispatcher.InvokeAsync(() => StopEntry(entryId, initiatedByDeploy: true));

        try
        {
            await WaitForProcessExitAsync(entryId, proc, TimeSpan.FromSeconds(20));
            return !_processes.ContainsKey(entryId);
        }
        catch (TimeoutException ex)
        {
            AppendDeployLog(entryId, ex.Message);
            return false;
        }
    }

    private async Task<bool> StartEntryForDeployAsync(string entryId)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => RunEntry(entryId, initiatedByDeploy: true));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (_processes.ContainsKey(entryId))
                return true;

            await Task.Delay(200);
        }

        return _processes.ContainsKey(entryId);
    }

    private async Task<bool> WaitForDeployValidationAsync(string entryId, ScriptEntry entry)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(5, entry.GitHealthTimeoutSeconds));
        var deadline = DateTime.UtcNow.Add(timeout);
        var stabilityDeadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (!_processes.TryGetValue(entryId, out var proc) || proc.HasExited)
                return false;

            if (entry.HealthCheckEnabled && !string.IsNullOrWhiteSpace(entry.HealthCheckUrl))
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var resp = await _httpClient.GetAsync(entry.HealthCheckUrl, cts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        var vm = GetScriptVm(entryId);
                        if (vm != null) vm.HealthStatus = "OK";
                        return true;
                    }
                }
                catch
                {
                }
            }
            else if (DateTime.UtcNow >= stabilityDeadline)
            {
                return true;
            }

            await Task.Delay(1500);
        }

        return false;
    }

    private static async Task WaitForProcessExitAsync(string entryId, Process proc, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Timed out while waiting for {entryId} to stop.");
        }
    }

    private async void RunEntry(string entryId, bool initiatedByDeploy = false)
    {
        if (!_entries.TryGetValue(entryId, out var entry)) return;
        if (!initiatedByDeploy && IsEntryDeploying(entryId))
        {
            MessageBox.Show("Wait for the deploy to finish before starting this script.", "Deploy in progress",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_processes.ContainsKey(entryId))
        {
            MessageBox.Show("This script is already running.", "Already running", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // ── Free port (kill whatever holds it) before starting ────
        if (entry.FreePort > 0)
        {
            AppendLog(entryId, $"[PORT] Freeing port {entry.FreePort}...");
            var port = entry.FreePort;
            await Task.Run(() => PortUtil.FreePort(port, msg => QueueLog(entryId, $"[PORT] {msg}")));
        }

        // ── Pre-start hook (all types) ────────────────────────────
        if (!string.IsNullOrWhiteSpace(entry.PreStartCommand))
        {
            AppendLog(entryId, "[PRE-START] Running hook...");
            await Task.Run(() => RunShellCommand(entryId, entry.PreStartCommand, "PRE-START"));
        }

        // ── Route to type-specific execution ─────────────────────
        if (entry.IsPsCommand)  { RunPsCommandEntry(entryId, entry); return; }
        if (entry.IsNpmCommand) { RunNpmEntry(entryId, entry);       return; }

        if (!File.Exists(entry.Path))
        {
            MessageBox.Show($"Script not found:\n{entry.Path}", "Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var wd = string.IsNullOrWhiteSpace(entry.WorkDir)
            ? System.IO.Path.GetDirectoryName(entry.Path)!
            : entry.WorkDir;

        var psi = new ProcessStartInfo
        {
            WorkingDirectory       = wd,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8
        };

        string tipoStr;
        if (entry.IsBatchFile)
        {
            psi.FileName  = "cmd.exe";
            var batchArgs = $"/c \"{entry.Path}\"";
            if (!string.IsNullOrEmpty(entry.Args)) batchArgs += $" {entry.Args}";
            psi.Arguments = batchArgs;
            tipoStr = "BATCH SCRIPT";
        }
        else if (entry.IsExecutable)
        {
            psi.FileName = entry.Path;
            if (!string.IsNullOrEmpty(entry.Args)) psi.Arguments = entry.Args;
            tipoStr = "NATIVE EXECUTABLE";
        }
        else if (entry.IsPsFile)
        {
            psi.FileName  = "powershell.exe";
            psi.Arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{entry.Path}\"";
            if (!string.IsNullOrEmpty(entry.Args)) psi.Arguments += $" {entry.Args}";
            tipoStr = "POWERSHELL SCRIPT";
        }
        else
        {
            var (program, extraArgs) = PythonDetector.SplitInterpreter(
                string.IsNullOrEmpty(entry.Interpreter) ? PythonDetector.Detect() : entry.Interpreter);
            psi.FileName = program;
            var allArgs  = new List<string>(extraArgs) { "-u", entry.Path };
            if (!string.IsNullOrEmpty(entry.Args))
                allArgs.AddRange(entry.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            psi.Arguments = string.Join(" ", allArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"]       = "1";
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            tipoStr = "PYTHON SCRIPT";
        }
        ConfigurePlainTextOutput(psi);

        var startStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        AppendLog(entryId, new string('=', 60));
        AppendLog(entryId, $"STARTING {tipoStr}: {entry.Name}");
        AppendLog(entryId, $"DATE/TIME : {startStamp}");
        AppendLog(entryId, $"PATH      : {entry.Path}");
        AppendLog(entryId, $"DIRECTORY : {wd}");
        AppendLog(entryId, $"PROGRAM   : {psi.FileName}");
        AppendLog(entryId, $"ARGUMENTS : {psi.Arguments}");
        AppendLog(entryId, new string('=', 60));
        AppendLog(entryId, "WAITING FOR OUTPUT...\n");

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            SetupProcessHandlers(entryId, entry, proc);
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AppendLog(entryId, $"[ERROR] Could not start: {ex.Message}");
            GetScriptVm(entryId)?.SetStatus("Error", "ErrorIcon");
            return;
        }

        RegisterProcess(entryId, proc);
        entry.LastRun        = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        entry.LastExitCode   = null;
        RequestConfigSave();
        StartHealthCheck(entryId, entry);

        var scriptVm = GetScriptVm(entryId);
        if (scriptVm != null)
        {
            scriptVm.LastRun = entry.LastRun;
            scriptVm.SetStatus($"Running (PID {proc.Id})", "ConnectedIcon");
        }
        UpdateGlobalStatus();
    }

    private void StopEntry(string entryId, bool initiatedByDeploy = false)
    {
        if (!initiatedByDeploy && IsEntryDeploying(entryId))
        {
            MessageBox.Show("Wait for the deploy to finish before stopping this script.", "Deploy in progress",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_processes.TryGetValue(entryId, out var proc)) return;
        if (!_entries.TryGetValue(entryId, out var entry)) return;

        _manuallyStopped.Add(entryId);
        _restartAttempts.Remove(entryId);
        StopHealthCheck(entryId);
        AppendLog(entryId, "[INFO] Terminating...");

        // Process-tree termination uses WMI queries + taskkill /T /F with a 5s
        // wait, which blocks for hundreds of ms to several seconds. Run it off
        // the UI thread so the app doesn't freeze on Stop/Run.
        var npmWorkDir = entry.IsNpmCommand
            ? (string.IsNullOrWhiteSpace(entry.WorkDir) ? Environment.CurrentDirectory : entry.WorkDir)
            : null;
        var npmScript = entry.IsNpmCommand ? entry.NpmScript : null;
        var rootPid = proc.Id;

        _ = Task.Run(() => TerminateEntryProcesses(entryId, rootPid, npmWorkDir, npmScript, proc));

        UpdateGlobalStatus();
    }

    private void TerminateEntryProcesses(string entryId, int rootPid, string? npmWorkDir, string? npmScript, Process proc)
    {
        // 1. Kill the whole tree via the Job Object (reliable for cmd→npm→node).
        if (_jobs.TryGetValue(entryId, out var job))
        {
            _jobs.Remove(entryId);
            try { job.Dispose(); } catch { } // KILL_ON_JOB_CLOSE terminates the tree
        }

        // 2. Belt-and-suspenders: taskkill /T on the root in case the job missed it.
        try
        {
            var killed = _resourceMonitorService.TerminateProcessTree(rootPid);
            if (killed > 0)
                QueueLog(entryId, $"[INFO] Terminated process tree ({killed} processes).");
        }
        catch
        {
            try { proc.Kill(entireProcessTree: true); }
            catch { try { proc.Kill(); } catch { } }
        }

        if (npmWorkDir == null)
            return;

        var extraKilled = _resourceMonitorService.TerminateLikelyNpmProcesses(npmWorkDir, npmScript ?? string.Empty);
        if (extraKilled > 0)
            QueueLog(entryId, $"[INFO] Terminated remaining npm/node processes ({extraKilled}).");
    }

    // ═══════ PowerShell Command Execution ═══════

    private static readonly string PsTempDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".script_control_center", "ps_temp");

    private void RunPsCommandEntry(string entryId, ScriptEntry entry)
    {
        Directory.CreateDirectory(PsTempDir);

        // Write body to a temp .ps1 file so multi-line scripts work correctly.
        // Strip interactive-shell prompt prefixes (">> " / "PS C:\...> ") that users may have pasted.
        var cleanBody = StripPsPromptPrefixes(entry.PsBody);
        var tempFile = Path.Combine(PsTempDir, $"cmd_{entryId}.ps1");
        try
        {
            File.WriteAllText(tempFile, cleanBody, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppendLog(entryId, $"[ERROR] Could not write temp script: {ex.Message}");
            GetScriptVm(entryId)?.SetStatus("Error", "ErrorIcon");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName  = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -NoProfile -NonInteractive -File \"{tempFile}\"",
            UseShellExecute          = false,
            RedirectStandardOutput   = true,
            RedirectStandardError    = true,
            CreateNoWindow           = true,
            StandardOutputEncoding   = Encoding.UTF8,
            StandardErrorEncoding    = Encoding.UTF8,
        };
        ConfigurePlainTextOutput(psi);

        var startStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        AppendLog(entryId, new string('=', 60));
        AppendLog(entryId, $"STARTING POWERSHELL COMMAND: {entry.Name}");
        AppendLog(entryId, $"DATE/TIME : {startStamp}");
        if (entry.RunAsAdmin)
            AppendLog(entryId, "[INFO] Run as Administrator flag is set — ensure this app is elevated.");
        AppendLog(entryId, $"TEMP FILE : {tempFile}");
        AppendLog(entryId, new string('-', 60));
        AppendLog(entryId, entry.PsBody);
        AppendLog(entryId, new string('=', 60));
        AppendLog(entryId, "WAITING FOR OUTPUT...\n");

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            SetupProcessHandlers(entryId, entry, proc);
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AppendLog(entryId, $"[ERROR] Could not start PowerShell: {ex.Message}");
            GetScriptVm(entryId)?.SetStatus("Error", "ErrorIcon");
            return;
        }

        RegisterProcess(entryId, proc);
        entry.LastRun      = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        entry.LastExitCode = null;
        RequestConfigSave();
        StartHealthCheck(entryId, entry);

        var scriptVm = GetScriptVm(entryId);
        if (scriptVm != null)
        {
            scriptVm.LastRun = entry.LastRun;
            scriptVm.SetStatus($"Running (PID {proc.Id})", "ConnectedIcon");
        }
        UpdateGlobalStatus();
    }

    // ═══════ Shared process helpers ═══════

    /// <summary>
    /// Wires stdout/stderr capture, exit handling (auto-restart, post-stop hook,
    /// health-check teardown) for any process type.
    /// </summary>
    private void SetupProcessHandlers(string entryId, ScriptEntry entry, Process proc)
    {
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                QueueLog(entryId, $"[STDOUT] {e.Data}");
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                QueueLog(entryId, $"[STDERR] {e.Data}");
        };
        proc.Exited += (_, _) =>
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                var exitCode = proc.ExitCode;
                entry.LastExitCode = exitCode;
                RequestConfigSave();

                var vm = GetScriptVm(entryId);
                if (vm != null)
                {
                    vm.LastExitCode  = exitCode;
                    vm.HealthStatus  = string.Empty;
                    vm.SetStatus($"Exit {exitCode}", "DisconnectedIcon");
                }

                AppendLog(entryId, $"=== EXIT code {exitCode} ===");
                _processes.Remove(entryId);
                // Free the job handle (also kills any lingering children of this entry).
                if (_jobs.TryGetValue(entryId, out var job))
                {
                    _jobs.Remove(entryId);
                    try { job.Dispose(); } catch { }
                }
                StopHealthCheck(entryId);

                // Post-stop hook
                if (!string.IsNullOrWhiteSpace(entry.PostStopCommand))
                    _ = Task.Run(() => RunShellCommand(entryId, entry.PostStopCommand, "POST-STOP"));

                bool wasManuallyStopped = _manuallyStopped.Remove(entryId);

                // Auto-restart on any unexpected exit (manual stop is excluded via _manuallyStopped)
                if (!wasManuallyStopped && entry.AutoRestart && !IsEntryDeploying(entryId))
                {
                    _restartAttempts.TryGetValue(entryId, out int attempts);
                    bool withinLimit = entry.MaxRestartAttempts <= 0 || attempts < entry.MaxRestartAttempts;

                    if (withinLimit)
                    {
                        _restartAttempts[entryId] = attempts + 1;
                        var delay  = Math.Max(1, entry.RestartDelaySeconds);
                        var maxStr = entry.MaxRestartAttempts <= 0 ? "∞" : entry.MaxRestartAttempts.ToString();
                        AppendLog(entryId, $"[AUTO-RESTART] Attempt {attempts + 1}/{maxStr} — restarting in {delay}s...");
                        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
                        t.Tick += (_, _) => { t.Stop(); RunEntry(entryId); };
                        t.Start();
                    }
                    else
                    {
                        AppendLog(entryId, $"[AUTO-RESTART] Max attempts ({entry.MaxRestartAttempts}) reached.");
                        _restartAttempts.Remove(entryId);
                    }
                }
                else
                {
                    // Manually stopped or auto-restart disabled — reset counter
                    _restartAttempts.Remove(entryId);
                }

                UpdateGlobalStatus();
            }), DispatcherPriority.Background);
        };
    }

    // ═══════ npm execution ═══════

    private void RunNpmEntry(string entryId, ScriptEntry entry)
    {
        var wd = string.IsNullOrWhiteSpace(entry.WorkDir) ? Environment.CurrentDirectory : entry.WorkDir;

        var psi = new ProcessStartInfo
        {
            FileName               = "cmd.exe",
            Arguments              = $"/c npm run {entry.NpmScript}",
            WorkingDirectory       = wd,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        ConfigurePlainTextOutput(psi);

        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        AppendLog(entryId, new string('=', 60));
        AppendLog(entryId, $"STARTING NPM SCRIPT: {entry.Name}");
        AppendLog(entryId, $"DATE/TIME : {stamp}");
        AppendLog(entryId, $"COMMAND   : npm run {entry.NpmScript}");
        AppendLog(entryId, $"DIRECTORY : {wd}");
        AppendLog(entryId, new string('=', 60));
        AppendLog(entryId, "WAITING FOR OUTPUT...\n");

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            SetupProcessHandlers(entryId, entry, proc);
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AppendLog(entryId, $"[ERROR] Could not start npm: {ex.Message}");
            GetScriptVm(entryId)?.SetStatus("Error", "ErrorIcon");
            return;
        }

        RegisterProcess(entryId, proc);
        entry.LastRun       = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        entry.LastExitCode  = null;
        RequestConfigSave();
        StartHealthCheck(entryId, entry);

        var scriptVm = GetScriptVm(entryId);
        if (scriptVm != null)
        {
            scriptVm.LastRun = entry.LastRun;
            scriptVm.SetStatus($"Running (PID {proc.Id})", "ConnectedIcon");
        }
        UpdateGlobalStatus();
    }

    // ═══════ Health check ═══════

    private void StartHealthCheck(string entryId, ScriptEntry entry)
    {
        if (!entry.HealthCheckEnabled || string.IsNullOrWhiteSpace(entry.HealthCheckUrl)) return;

        StopHealthCheck(entryId);
        _healthFailures[entryId] = 0;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(5, entry.HealthCheckIntervalSeconds))
        };
        timer.Tick += async (_, _) => await DoHealthCheckAsync(entryId, entry);
        timer.Start();
        _healthTimers[entryId] = timer;
    }

    private void StopHealthCheck(string entryId)
    {
        if (_healthTimers.TryGetValue(entryId, out var t)) { t.Stop(); _healthTimers.Remove(entryId); }
        _healthFailures.Remove(entryId);
    }

    private async Task DoHealthCheckAsync(string entryId, ScriptEntry entry)
    {
        if (!_processes.ContainsKey(entryId)) { StopHealthCheck(entryId); return; }
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            var resp = await _httpClient.GetAsync(entry.HealthCheckUrl, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                _healthFailures[entryId] = 0;
                var vm = GetScriptVm(entryId);
                if (vm != null) vm.HealthStatus = "OK";
            }
            else
            {
                RecordHealthFailure(entryId, entry, $"HTTP {(int)resp.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Length > 60 ? ex.Message[..60] : ex.Message;
            RecordHealthFailure(entryId, entry, msg);
        }
    }

    private void RecordHealthFailure(string entryId, ScriptEntry entry, string reason)
    {
        _healthFailures.TryGetValue(entryId, out int n);
        n++;
        _healthFailures[entryId] = n;

        AppendLog(entryId, $"[HEALTH] FAIL ({n}/{entry.HealthCheckFailuresBeforeRestart}): {reason}");
        var vm = GetScriptVm(entryId);
        if (vm != null) vm.HealthStatus = $"FAIL ({n})";

        if (n < entry.HealthCheckFailuresBeforeRestart) return;

        if (IsEntryDeploying(entryId))
        {
            AppendLog(entryId, "[HEALTH] Deploy validation in progress - auto-restart suppressed.");
            return;
        }

        AppendLog(entryId, "[HEALTH] Threshold reached — restarting...");
        StopHealthCheck(entryId);

        // Kill without triggering auto-restart from proc.Exited (we schedule our own).
        // Run the heavy WMI/taskkill work off the UI thread to avoid freezing.
        if (_processes.TryGetValue(entryId, out var proc))
        {
            _manuallyStopped.Add(entryId);
            var npmWorkDir = entry.IsNpmCommand
                ? (string.IsNullOrWhiteSpace(entry.WorkDir) ? Environment.CurrentDirectory : entry.WorkDir)
                : null;
            var npmScript = entry.IsNpmCommand ? entry.NpmScript : null;
            var rootPid = proc.Id;
            _ = Task.Run(() => TerminateEntryProcesses(entryId, rootPid, npmWorkDir, npmScript, proc));
        }

        var delay = Math.Max(1, entry.RestartDelaySeconds);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        t.Tick += (_, _) => { t.Stop(); RunEntry(entryId); };
        t.Start();
    }

    // ═══════ Shell command helper (pre/post hooks) ═══════

    private void RunShellCommand(string entryId, string command, string label)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "cmd.exe",
                Arguments              = $"/c {command}",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };
            ConfigurePlainTextOutput(psi);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);

            if (!string.IsNullOrWhiteSpace(stdout))
                QueueLog(entryId, $"[{label}] {stdout.Trim()}");
            if (!string.IsNullOrWhiteSpace(stderr))
                QueueLog(entryId, $"[{label}] STDERR: {stderr.Trim()}");
            QueueLog(entryId, $"[{label}] exit {p.ExitCode}");
        }
        catch (Exception ex)
        {
            QueueLog(entryId, $"[{label}] failed: {ex.Message}");
        }
    }

    private static string StripPsPromptPrefixes(string body)
    {
        var lines = body.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith(">> "))      { lines[i] = line[3..]; continue; }
            if (line.StartsWith(">>"))       { lines[i] = line[2..]; continue; }
            if (line.StartsWith("PS ") && line.Contains("> "))
            {
                var idx = line.IndexOf("> ") + 2;
                lines[i] = line[idx..];
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    // ═══════ Auto-Start ═══════

    private void AutoStartScripts()
    {
        var autoStarts = _entries.Values.Where(e => e.AutoStart).ToList();
        if (autoStarts.Count > 0)
        {
            AppendLog("system", $"\n=== AUTO-START: Running {autoStarts.Count} marked scripts ===");
            foreach (var entry in autoStarts)
            {
                AppendLog("system", $"Starting: {entry.Name}");
                RunEntry(entry.Id);
            }
        }
    }

    // ═══════ Global Status ═══════

    private void UpdateGlobalStatus()
    {
        var total = _entries.Count;
        var running = _processes.Count;

        if (total == 0)
        {
            GlobalStatusImageUri = "pack://application:,,,/Resources/DESCONECTADO.png";
            GlobalStatusText = "No scripts";
            GlobalStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4a5568"));
            return;
        }

        if (running == total)
        {
            GlobalStatusImageUri = "pack://application:,,,/Resources/CONECTADO.png";
            GlobalStatusText = $"All running ({running}/{total})";
            GlobalStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3fb950"));
        }
        else if (running > 0)
        {
            GlobalStatusImageUri = "pack://application:,,,/Resources/DESCONECTADO.png";
            GlobalStatusText = $"Partial ({running}/{total})";
            GlobalStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#d29922"));
        }
        else
        {
            GlobalStatusImageUri = "pack://application:,,,/Resources/DESCONECTADO.png";
            GlobalStatusText = $"Stopped (0/{total})";
            GlobalStatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f85149"));
        }
    }

    // ═══════ Cleanup ═══════

    public void Shutdown()
    {
        _logFlushTimer?.Stop();
        _logUiTimer?.Stop();
        _configSaveTimer?.Stop();
        _resourceMonitorTimer?.Stop();
        _temperatureMonitorTimer?.Stop();
        _databaseMonitorTimer?.Stop();
        _gitCommitCts?.Cancel();

        foreach (var t in _healthTimers.Values) t.Stop();
        _healthTimers.Clear();

        DrainQueuedLogsAndRefreshView(int.MaxValue);
        FlushPendingConfigSave();

        // Flush remaining logs to disk before exit
        FlushAndClearLogs();

        // On shutdown we terminate synchronously so processes don't outlive the
        // app (the window is closing; a brief block here is acceptable).
        foreach (var (entryId, proc) in _processes.ToList())
        {
            if (_entries.TryGetValue(entryId, out var entry))
            {
                var npmWorkDir = entry.IsNpmCommand
                    ? (string.IsNullOrWhiteSpace(entry.WorkDir) ? Environment.CurrentDirectory : entry.WorkDir)
                    : null;
                var npmScript = entry.IsNpmCommand ? entry.NpmScript : null;
                TerminateEntryProcesses(entryId, proc.Id, npmWorkDir, npmScript, proc);
            }
            else
                try { _resourceMonitorService.TerminateProcessTree(proc.Id); } catch { }
        }
        _processes.Clear();
    }
}
