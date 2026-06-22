using MesControlCenter.Core.Services;
using Microsoft.Extensions.Hosting;

namespace MesControlCenter.Agent;

/// <summary>
/// Hosted service that runs the WebSocket agent client. All MySQL access now
/// lives on the server side; this process only talks WS.
/// </summary>
public class AgentWorker : BackgroundService
{
    private readonly WsAgentClient _client;

    public AgentWorker(WsAgentClient client)
    {
        _client = client;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log("=== PC MONITOR AGENT STARTED (WebSocket) ===");
        await _client.RunAsync(stoppingToken);
    }

    private static void Log(string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        Console.WriteLine($"[{ts}] [AGENT] {message}");
    }
}
