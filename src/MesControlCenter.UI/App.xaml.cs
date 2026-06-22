using System.Windows;
using System.Windows.Threading;
using MesControlCenter.Core.Interfaces;
using MesControlCenter.Core.Services;
using MesControlCenter.Data;
using MesControlCenter.UI.ViewModels;
using MesControlCenter.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlCenter.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Catch unhandled exceptions so the app doesn't crash silently
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // The WS server URL is resolved at runtime (env var or encrypted per-user
        // file); MySQL credentials no longer live on client machines. If no server
        // is configured, warn but keep running so the user can configure it.
        if (!ClientConfig.IsConfigured())
        {
            MessageBox.Show(
                "No se ha configurado el servidor de MES Control Center.\n\n" +
                $"Define la variable de entorno '{ClientConfig.EnvVarName}' " +
                "(p.ej. ws://host:8092/ws) o ejecuta el asistente de instalación.\n\n" +
                "La aplicación abrirá, pero el monitoreo remoto estará inactivo hasta configurarlo.",
                "MES Control Center — Configuración requerida",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        var services = new ServiceCollection();

        // Core services
        services.AddSingleton<ICredentialService, CredentialService>();
        services.AddSingleton<IScriptMonitor, ProcessMonitorService>();
        services.AddSingleton<CommandExecutorService>();
        services.AddSingleton<GitDeployService>();
        services.AddSingleton<ResourceMonitorService>();
        services.AddSingleton<IScriptConfigRepository, JsonScriptConfigRepository>();

        // WebSocket agent client (replaces the old direct-MySQL agent loop)
        services.AddSingleton(sp => new WsAgentClient(
            sp.GetRequiredService<ICredentialService>(),
            sp.GetRequiredService<IScriptMonitor>(),
            sp.GetRequiredService<CommandExecutorService>(),
            sp.GetRequiredService<IScriptConfigRepository>(),
            ClientConfig.ResolveServerUrl));

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<ScriptEditorViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<InstallerViewModel>();

        Services = services.BuildServiceProvider();

        var mainWindow = new MainWindow
        {
            DataContext = Services.GetRequiredService<MainViewModel>()
        };
        mainWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Console.WriteLine($"[ERROR] {e.Exception.Message}");
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        Dispatcher.Invoke(() =>
        {
            Console.WriteLine($"[UNOBSERVED] {e.Exception?.InnerException?.Message ?? e.Exception?.Message}");
        });
    }
}
