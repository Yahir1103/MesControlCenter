using System.Windows;
using System.Windows.Input;
using MesControlCenter.UI.ViewModels;

namespace MesControlCenter.UI.Views;

public partial class InstallerWizardWindow : Window
{
    public InstallerWizardWindow()
    {
        InitializeComponent();
        DataContext = new InstallerViewModel();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        await RunInstall();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = RunInstall();
        }
    }

    private async Task RunInstall()
    {
        if (DataContext is not InstallerViewModel vm) return;
        if (vm.IsComplete)
        {
            DialogResult = true;
            return;
        }

        if (vm.InstallCommand.CanExecute(null))
        {
            await vm.InstallCommand.ExecuteAsync(null);
            if (vm.IsComplete)
                DialogResult = true;
        }
    }
}
