using System.ComponentModel;
using System.Windows;
using MesControlCenter.UI.ViewModels;

namespace MesControlCenter.UI.Views;

public partial class BackupWindow : Window
{
    public BackupWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is BackupViewModel vm)
            await vm.StartAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (DataContext is BackupViewModel vm)
            await vm.StopAsync();
    }
}
