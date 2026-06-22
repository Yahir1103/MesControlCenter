using MesControlCenter.Agent;
using MesControlCenter.Core.Interfaces;
using MesControlCenter.Core.Services;
using MesControlCenter.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] === MES Control Center Agent ===");
Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Version: 3.0.0 (WebSocket)");

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<ICredentialService, CredentialService>();
builder.Services.AddSingleton<IScriptConfigRepository, JsonScriptConfigRepository>();
builder.Services.AddSingleton<IScriptMonitor, ProcessMonitorService>();
builder.Services.AddSingleton<CommandExecutorService>();

builder.Services.AddSingleton(sp => new WsAgentClient(
    sp.GetRequiredService<ICredentialService>(),
    sp.GetRequiredService<IScriptMonitor>(),
    sp.GetRequiredService<CommandExecutorService>(),
    sp.GetRequiredService<IScriptConfigRepository>(),
    ClientConfig.ResolveServerUrl));

builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
await host.RunAsync();
