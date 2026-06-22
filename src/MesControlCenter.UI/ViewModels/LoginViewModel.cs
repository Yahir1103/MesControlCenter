using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MesControlCenter.UI.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private const string ValidUsername = "IT";
    private const string ValidPassword = "IT2025";

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public bool IsAuthenticated { get; private set; }

    public bool TryLogin()
    {
        if (Username == ValidUsername && Password == ValidPassword)
        {
            IsAuthenticated = true;
            ErrorMessage = string.Empty;
            return true;
        }

        ErrorMessage = "Invalid username or password.";
        return false;
    }
}
