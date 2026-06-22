using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MesControlCenter.Core.Interfaces;
using MesControlCenter.Core.Models;

namespace MesControlCenter.Core.Services;

/// <summary>
/// Cliente WebSocket del agente. Reemplaza el polling directo a MySQL:
/// se conecta al servidor intermedio, se autentica con pc_key/api_secret,
/// publica heartbeat y estado de scripts, y ejecuta comandos recibidos por push.
/// Reconecta con backoff si el socket cae.
/// </summary>
public class WsAgentClient
{
    private readonly ICredentialService _credentials;
    private readonly IScriptMonitor _monitor;
    private readonly CommandExecutorService _executor;
    private readonly IScriptConfigRepository _configRepo;
    private readonly Func<string> _serverUrlProvider;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public WsAgentClient(
        ICredentialService credentials,
        IScriptMonitor monitor,
        CommandExecutorService executor,
        IScriptConfigRepository configRepo,
        Func<string> serverUrlProvider)
    {
        _credentials = credentials;
        _monitor = monitor;
        _executor = executor;
        _configRepo = configRepo;
        _serverUrlProvider = serverUrlProvider;
    }

    /// <summary>Optional hook so a host (UI/console) can surface log lines.</summary>
    public Action<string>? OnLog { get; set; }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var config = _credentials.LoadConfig();
        if (config == null)
        {
            Log("[ERROR] No configuration found. Run the installer first.");
            return;
        }

        ReloadScripts();

        var backoff = TimeSpan.FromSeconds(2);
        var maxBackoff = TimeSpan.FromSeconds(30);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndServeAsync(config, stoppingToken);
                // Si vuelve sin excepción, reinicia backoff.
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"[WS] Disconnected: {ex.Message}. Reintentando en {backoff.TotalSeconds:0}s...");
            }

            try { await Task.Delay(backoff, stoppingToken); }
            catch (OperationCanceledException) { break; }

            backoff = TimeSpan.FromSeconds(Math.Min(maxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }

        Log("[WS] Agent client stopped.");
    }

    private async Task ConnectAndServeAsync(PcMonitorConfig config, CancellationToken stoppingToken)
    {
        var url = _serverUrlProvider();
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Server URL not configured (MESCC_SERVER_URL).");

        using var ws = new ClientWebSocket();
        Log($"[WS] Connecting to {url}...");
        await ws.ConnectAsync(new Uri(url), stoppingToken);
        Log("[WS] Connected. Authenticating...");

        // 1. Auth
        await SendAsync(ws, new
        {
            type = "auth",
            role = "agent",
            pc_key = config.PcKey,
            api_secret = config.ApiSecret,
            pc_name = config.PcName
        }, stoppingToken);

        // 2. Lanzar el bucle de envío (heartbeat + script_status) en paralelo a la recepción.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var sendLoop = SendLoopAsync(ws, linkedCts.Token);

        try
        {
            await ReceiveLoopAsync(ws, stoppingToken);
        }
        finally
        {
            linkedCts.Cancel();
            try { await sendLoop; } catch { /* ignore */ }
        }
    }

    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        int cycle = 0;
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            cycle++;
            try
            {
                if (cycle % 6 == 0) ReloadScripts(); // recarga scripts cada ~30s

                // Heartbeat
                bool allActive = _monitor.AreAllScriptsActive();
                await SendAsync(ws, new { type = "heartbeat", active = allActive }, token);

                // Estado de scripts
                var statuses = _monitor.GetAllStatuses();
                var scripts = statuses.Select(s => new
                {
                    name = s.ScriptName,
                    active = s.IsActive,
                    extra = new { pid = s.Pid, cpu_percent = s.CpuPercent, memory_mb = s.MemoryMb, status = s.Status }
                }).ToArray();
                await SendAsync(ws, new { type = "script_status", scripts }, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"[WS] Send loop error: {ex.Message}");
                break;
            }

            try { await Task.Delay(5000, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var sb = new StringBuilder();

        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            await HandleServerMessageAsync(ws, sb.ToString(), token);
        }
    }

    private async Task HandleServerMessageAsync(ClientWebSocket ws, string json, CancellationToken token)
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
                case "auth_ok":
                    Log("[WS] Authenticated.");
                    break;

                case "auth_error":
                    Log($"[WS] [ERROR] Auth rejected: {root.GetProperty("error").GetString()}");
                    throw new InvalidOperationException("Authentication failed");

                case "command":
                    await HandleCommandAsync(ws, root, token);
                    break;

                case "error":
                    Log($"[WS] Server error: {root.GetProperty("error").GetString()}");
                    break;
            }
        }
    }

    private async Task HandleCommandAsync(ClientWebSocket ws, JsonElement root, CancellationToken token)
    {
        long commandId = root.TryGetProperty("command_id", out var cid) ? cid.GetInt64() : 0;
        var command = root.TryGetProperty("command", out var c) ? c.GetString() : null;
        string? payload = null;
        if (root.TryGetProperty("payload", out var p) && p.ValueKind != JsonValueKind.Null)
            payload = p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText();

        if (string.IsNullOrEmpty(command))
            return;

        Log($"[WS] Command received: {command} (id {commandId})");

        var (success, resultMsg) = _executor.ExecuteCommand(command, payload);

        await SendAsync(ws, new
        {
            type = "command_result",
            command_id = commandId,
            status = success ? "done" : "failed",
            result_msg = resultMsg
        }, token);
    }

    private void ReloadScripts()
    {
        var entries = _configRepo.Load();
        var scriptNames = entries.Select(e => Path.GetFileName(e.Path)).ToList();
        _monitor.UpdateScripts(scriptNames);
        _executor.UpdateEntries(entries.ToList());
    }

    private static async Task SendAsync(ClientWebSocket ws, object obj, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(obj, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
    }

    private void Log(string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{ts}] [AGENT] {message}";
        Console.WriteLine(line);
        OnLog?.Invoke(line);
    }
}
