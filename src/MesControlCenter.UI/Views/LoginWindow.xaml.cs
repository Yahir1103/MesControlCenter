using System.Windows;
using System.Windows.Input;
using MesControlCenter.UI.ViewModels;

namespace MesControlCenter.UI.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;

    public LoginWindow()
    {
        InitializeComponent();
        _vm = new LoginViewModel();
        DataContext = _vm;
        TxtUsername.Focus();
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        DoLogin();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            DoLogin();
    }

    private void DoLogin()
    {
        _vm.Password = PwdBox.Password;
        if (_vm.TryLogin())
            DialogResult = true;
    }
}
