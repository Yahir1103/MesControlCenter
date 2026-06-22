using System.ComponentModel;
using System.Windows;
using MesControlCenter.UI.ViewModels;

namespace MesControlCenter.UI.Views;

public partial class BackupWindow : Window
{
    private bool _syncingPassword;

    public BackupWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is BackupViewModel vm)
        {
            await vm.StartAsync();
            SyncPasswordBox(vm.DbPassword);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (DataContext is BackupViewModel vm)
            await vm.StopAsync();
    }

    private void DbPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword)
            return;

        if (DataContext is BackupViewModel vm)
            vm.DbPassword = DbPasswordBox.Password;
    }

    private void SyncPasswordBox(string password)
    {
        try
        {
            _syncingPassword = true;
            DbPasswordBox.Password = password;
        }
        finally
        {
            _syncingPassword = false;
        }
    }
}
