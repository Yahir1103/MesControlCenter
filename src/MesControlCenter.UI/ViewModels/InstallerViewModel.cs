using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MesControlCenter.Core.Interfaces;
using MesControlCenter.Core.Services;

namespace MesControlCenter.UI.ViewModels;

public partial class InstallerViewModel : ObservableObject
{
    private readonly ICredentialService _credentials;

    [ObservableProperty] private string _pcName = Environment.MachineName;
    [ObservableProperty] private string _serverUrl = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public InstallerViewModel(ICredentialService credentials)
    {
        _credentials = credentials;
        // Prefill with whatever is already configured, if anything.
        try { ServerUrl = ClientConfig.ResolveServerUrl(); } catch { ServerUrl = string.Empty; }
    }

    public InstallerViewModel() : this(
        (ICredentialService)App.Services.GetService(typeof(ICredentialService))!)
    { }

    [RelayCommand]
    private Task InstallAsync()
    {
        ErrorMessage = string.Empty;

        var name = PcName.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 50)
        {
            ErrorMessage = "PC name must be 1-50 characters.";
            return Task.CompletedTask;
        }

        var url = ServerUrl.Trim();
        if (string.IsNullOrEmpty(url) || !(url.StartsWith("ws://") || url.StartsWith("wss://")))
        {
            ErrorMessage = "La URL del servidor debe empezar con ws:// o wss://";
            return Task.CompletedTask;
        }

        try
        {
            // Step 1: credenciales locales (pc_key/api_secret)
            StatusMessage = "Paso 1/3: Generando credenciales...";
            Progress = 33;

            var existingConfig = _credentials.LoadConfig();
            var pcKey = existingConfig?.PcKey ?? _credentials.GeneratePcKey(name);
            var apiSecret = existingConfig?.ApiSecret ?? _credentials.GenerateApiSecret();

            // Step 2: guardar config local
            StatusMessage = "Paso 2/3: Guardando configuración local...";
            Progress = 66;

            if (!_credentials.SaveConfig(pcKey, apiSecret, name, new List<string>()))
            {
                ErrorMessage = "No se pudo guardar la configuración local.";
                return Task.CompletedTask;
            }

            // Step 3: guardar URL del servidor (cifrada, DPAPI)
            ClientConfig.SaveServerUrl(url);

            // El registro en la DB ya NO se hace aquí: el servidor registra la PC
            // (TOFU) en el primer auth por WebSocket.
            StatusMessage = "Paso 3/3: Configuración completa. El agente se conectará al servidor.";
            Progress = 100;
            IsComplete = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Installation failed: {ex.Message}";
        }

        return Task.CompletedTask;
    }
}
