using System.Windows;
using System.Windows.Threading;
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

        var services = new ServiceCollection();

        // Core services
        services.AddSingleton<GitDeployService>();
        services.AddSingleton<ResourceMonitorService>();
        services.AddSingleton<LocalBackupService>();
        services.AddSingleton<JsonScriptConfigRepository>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<BackupViewModel>();
        services.AddTransient<ScriptEditorViewModel>();

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
