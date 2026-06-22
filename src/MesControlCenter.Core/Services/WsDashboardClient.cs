using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MesControlCenter.Core.Models;

namespace MesControlCenter.Core.Services;

/// <summary>
/// WebSocket client for the admin dashboard and backup monitor.
/// </summary>
public class WsDashboardClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    // Pending request/response correlation for get_scripts.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<List<PcScript>>> _scriptWaiters = new();
    private TaskCompletionSource<BackupStatus>? _backupStatusWaiter;
    private TaskCompletionSource<List<BackupRun>>? _backupRunsWaiter;
    private TaskCompletionSource<BackupConfig>? _backupConfigWaiter;
    private TaskCompletionSource<bool>? _backupRunNowWaiter;

    // ─── Events for the ViewModel to subscribe to (raised off the UI thread) ───
    public event Action<List<PcInfo>>? SnapshotReceived;
    public event Action<string, bool, DateTime?>? PcUpdated;          // pcKey, isActive, lastSeen
    public event Action<string, string, bool>? ScriptUpdated;          // pcKey, scriptName, isActive
    public event Action<string>? PcDeleted;                            // pcKey
    public event Action<BackupStatus>? BackupUpdated;
    public event Action<string>? Disconnected;                         // reason

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(string url, string adminToken, CancellationToken ct = default)
        => await ConnectWithAuthAsync(url, new { type = "auth", role = "dashboard", token = adminToken }, ct);

    public async Task ConnectForBackupsAsync(string url, CancellationToken ct = default)
        => await ConnectWithAuthAsync(url, new { type = "auth", role = "backup" }, ct);

    private async Task ConnectWithAuthAsync(string url, object authMessage, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(url), _cts.Token);

        await SendAsync(authMessage);

        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public Task RefreshAsync() => SendAsync(new { type = "get_pcs" });

    public Task SendCommandAsync(string targetPcKey, string command, object? payload = null)
        => SendAsync(new { type = "command", target_pc_key = targetPcKey, command, payload });

    public Task DeletePcAsync(string pcKey) => SendAsync(new { type = "delete_pc", pc_key = pcKey });

    public async Task<BackupStatus> GetBackupStatusAsync(TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<BackupStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _backupStatusWaiter = tcs;
        await SendAsync(new { type = "get_backup_status" });

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        cts.Token.Register(() => tcs.TrySetException(new TimeoutException("get_backup_status timed out")));
        try { return await tcs.Task; }
        finally
        {
            if (ReferenceEquals(_backupStatusWaiter, tcs))
                _backupStatusWaiter = null;
        }
    }

    public async Task<List<BackupRun>> GetBackupRunsAsync(int limit = 50, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<List<BackupRun>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _backupRunsWaiter = tcs;
        await SendAsync(new { type = "get_backup_runs", limit });

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        cts.Token.Register(() => tcs.TrySetException(new TimeoutException("get_backup_runs timed out")));
        try { return await tcs.Task; }
        finally
        {
            if (ReferenceEquals(_backupRunsWaiter, tcs))
                _backupRunsWaiter = null;
        }
    }

    public async Task<BackupConfig> UpdateBackupConfigAsync(BackupConfig config, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<BackupConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        _backupConfigWaiter = tcs;
        await SendAsync(new
        {
            type = "update_backup_config",
            config = new
            {
                enabled = config.Enabled,
                backup_time = config.BackupTime,
                retention_days = config.RetentionDays,
                backup_dir = config.BackupDir
            }
        });

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        cts.Token.Register(() => tcs.TrySetException(new TimeoutException("update_backup_config timed out")));
        try { return await tcs.Task; }
        finally
        {
            if (ReferenceEquals(_backupConfigWaiter, tcs))
                _backupConfigWaiter = null;
        }
    }

    public async Task<bool> RunBackupNowAsync(TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _backupRunNowWaiter = tcs;
        await SendAsync(new { type = "run_backup_now" });

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        cts.Token.Register(() => tcs.TrySetException(new TimeoutException("run_backup_now timed out")));
        try { return await tcs.Task; }
        finally
        {
            if (ReferenceEquals(_backupRunNowWaiter, tcs))
                _backupRunNowWaiter = null;
        }
    }

    /// <summary>Requests the scripts of a PC and awaits the server response.</summary>
    public async Task<List<PcScript>> GetScriptsAsync(string pcKey, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<List<PcScript>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _scriptWaiters[pcKey] = tcs;
        await SendAsync(new { type = "get_scripts", pc_key = pcKey });

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        cts.Token.Register(() => tcs.TrySetException(new TimeoutException("get_scripts timed out")));
        try { return await tcs.Task; }
        finally { _scriptWaiters.TryRemove(pcKey, out _); }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (!token.IsCancellationRequested && _ws!.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Disconnected?.Invoke("server closed");
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                HandleMessage(sb.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Disconnected?.Invoke(ex.Message);
        }
    }

    private void HandleMessage(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return; }

        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "pcs_snapshot":
                    SnapshotReceived?.Invoke(ParsePcList(root.GetProperty("pcs")));
                    break;

                case "pc_update":
                    PcUpdated?.Invoke(
                        root.GetProperty("pc_key").GetString() ?? "",
                        root.TryGetProperty("is_active", out var ia) && ia.GetBoolean(),
                        ParseDate(root, "last_seen"));
                    break;

                case "script_update":
                    ScriptUpdated?.Invoke(
                        root.GetProperty("pc_key").GetString() ?? "",
                        root.GetProperty("script_name").GetString() ?? "",
                        root.TryGetProperty("is_active", out var sa) && sa.GetBoolean());
                    break;

                case "pc_deleted":
                    PcDeleted?.Invoke(root.GetProperty("pc_key").GetString() ?? "");
                    break;

                case "backup_status":
                    {
                        var status = ParseBackupStatus(root);
                        _backupStatusWaiter?.TrySetResult(status);
                        break;
                    }

                case "backup_runs":
                    _backupRunsWaiter?.TrySetResult(ParseBackupRunList(root.GetProperty("runs")));
                    break;

                case "backup_config_saved":
                    _backupConfigWaiter?.TrySetResult(ParseBackupConfig(root.GetProperty("config")));
                    break;

                case "backup_run_started":
                    _backupRunNowWaiter?.TrySetResult(
                        !root.TryGetProperty("started", out var started)
                        || started.ValueKind != JsonValueKind.False);
                    break;

                case "backup_update":
                    if (root.TryGetProperty("status", out var updateStatus))
                    {
                        var status = ParseBackupStatus(updateStatus);
                        BackupUpdated?.Invoke(status);
                    }
                    break;

                case "scripts":
                    {
                        var pcKey = root.GetProperty("pc_key").GetString() ?? "";
                        var scripts = ParseScriptList(root.GetProperty("scripts"));
                        if (_scriptWaiters.TryGetValue(pcKey, out var waiter))
                            waiter.TrySetResult(scripts);
                        break;
                    }
            }
        }
    }

    private static List<PcInfo> ParsePcList(JsonElement arr)
    {
        var list = new List<PcInfo>();
        foreach (var e in arr.EnumerateArray())
        {
            list.Add(new PcInfo
            {
                Id = GetLong(e, "id"),
                PcKey = GetString(e, "pc_key"),
                PcName = GetString(e, "pc_name"),
                Role = GetString(e, "role", "USER"),
                IsActive = GetLong(e, "is_active") == 1,
                LastSeen = ParseDate(e, "last_seen"),
                SecondsSinceSeen = e.TryGetProperty("seconds_since_seen", out var s) && s.ValueKind == JsonValueKind.Number
                    ? s.GetInt32() : (int?)null,
                CreatedAt = ParseDate(e, "created_at")
            });
        }
        return list;
    }

    private static BackupStatus ParseBackupStatus(JsonElement e)
    {
        return new BackupStatus
        {
            Config = e.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object
                ? ParseBackupConfig(config)
                : new BackupConfig(),
            IsRunning = e.TryGetProperty("is_running", out var running) && running.ValueKind == JsonValueKind.True,
            NextRunAt = ParseDate(e, "next_run_at"),
            LastRun = e.TryGetProperty("last_run", out var lastRun) && lastRun.ValueKind == JsonValueKind.Object
                ? ParseBackupRun(lastRun)
                : null
        };
    }

    private static BackupConfig ParseBackupConfig(JsonElement e)
    {
        return new BackupConfig
        {
            Enabled = e.TryGetProperty("enabled", out var enabled)
                && (enabled.ValueKind == JsonValueKind.True
                    || (enabled.ValueKind == JsonValueKind.Number && enabled.GetInt32() == 1)),
            BackupTime = GetString(e, "backup_time", "22:00"),
            RetentionDays = e.TryGetProperty("retention_days", out var retention)
                            && retention.ValueKind == JsonValueKind.Number
                ? retention.GetInt32()
                : 7,
            BackupDir = GetString(e, "backup_dir", "./backups"),
            Timezone = GetString(e, "timezone", "America/Mexico_City")
        };
    }

    private static List<BackupRun> ParseBackupRunList(JsonElement arr)
    {
        var list = new List<BackupRun>();
        foreach (var e in arr.EnumerateArray())
            list.Add(ParseBackupRun(e));
        return list;
    }

    private static BackupRun ParseBackupRun(JsonElement e)
    {
        return new BackupRun
        {
            Id = GetLong(e, "id"),
            RunType = GetString(e, "run_type"),
            Status = GetString(e, "status"),
            StartedAt = ParseDate(e, "started_at"),
            FinishedAt = ParseDate(e, "finished_at"),
            DurationMs = GetNullableInt(e, "duration_ms"),
            FilePath = GetNullableString(e, "file_path"),
            FileSizeBytes = GetNullableLong(e, "file_size_bytes"),
            ErrorMessage = GetNullableString(e, "error_message")
        };
    }

    private static List<PcScript> ParseScriptList(JsonElement arr)
    {
        var list = new List<PcScript>();
        foreach (var e in arr.EnumerateArray())
        {
            list.Add(new PcScript
            {
                Id = GetLong(e, "id"),
                ScriptName = GetString(e, "script_name"),
                IsActive = GetLong(e, "is_active") == 1,
                LastHeartbeat = ParseDate(e, "last_heartbeat"),
                ExtraStatus = e.TryGetProperty("extra_status", out var ex) && ex.ValueKind != JsonValueKind.Null
                    ? (ex.ValueKind == JsonValueKind.String ? ex.GetString() : ex.GetRawText())
                    : null,
                CreatedAt = ParseDate(e, "created_at")
            });
        }
        return list;
    }

    private static string GetString(JsonElement e, string name, string fallback = "")
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static int? GetNullableInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static long? GetNullableLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static string? GetNullableString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? ParseDate(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && DateTime.TryParse(v.GetString(), out var dt) ? dt : (DateTime?)null;

    private async Task SendAsync(object obj)
    {
        if (_ws is not { State: WebSocketState.Open }) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj, JsonOpts));
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
            _cts?.Token ?? CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { }
        }
        _ws?.Dispose();
        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { }
        }
        _cts?.Dispose();
    }
}
