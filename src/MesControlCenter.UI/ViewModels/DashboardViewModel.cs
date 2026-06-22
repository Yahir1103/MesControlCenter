using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MesControlCenter.Core.Models;
using MesControlCenter.Core.Services;
using MesControlCenter.UI.Views;

namespace MesControlCenter.UI.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly WsDashboardClient _ws = new();

    public ObservableCollection<PcCardViewModel> PcCards { get; } = new();

    [ObservableProperty]
    private string _statusMessage = "Connecting to server...";

    [ObservableProperty]
    private bool _isLoading = true;

    public DashboardViewModel()
    {
        _ws.SnapshotReceived += OnSnapshot;
        _ws.PcUpdated += OnPcUpdated;
        _ws.ScriptUpdated += OnScriptUpdated;
        _ws.PcDeleted += OnPcDeleted;
        _ws.Disconnected += OnDisconnected;
    }

    /// <summary>Connects to the WS server. Replaces the old 5s MySQL polling.</summary>
    public async Task StartAsync()
    {
        try
        {
            var url = ClientConfig.ResolveServerUrl();
            var token = ClientConfig.ResolveAdminToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                IsLoading = false;
                StatusMessage = "Falta el token admin (MESCC_ADMIN_TOKEN).";
                return;
            }
            await _ws.ConnectAsync(url, token);
            // The server pushes a pcs_snapshot right after auth_ok.
        }
        catch (Exception ex)
        {
            IsLoading = false;
            StatusMessage = $"Connection error: {ex.Message}";
        }
    }

    public async Task StopAsync() => await _ws.DisposeAsync();

    private void OnSnapshot(List<PcInfo> pcs)
    {
        OnUi(() =>
        {
            PcCards.Clear();
            foreach (var pc in pcs)
                PcCards.Add(new PcCardViewModel(pc));

            IsLoading = false;
            StatusMessage = PcCards.Count == 0
                ? "No PCs registered. Configure PC Monitor on your workstations."
                : $"{PcCards.Count} PC(s) found";
        });
    }

    private void OnPcUpdated(string pcKey, bool isActive, DateTime? lastSeen)
    {
        OnUi(() =>
        {
            var card = PcCards.FirstOrDefault(c => c.Data.PcKey == pcKey);
            if (card == null) return;
            card.Data.IsActive = isActive;
            card.Data.SecondsSinceSeen = isActive ? 0 : card.Data.SecondsSinceSeen;
            if (lastSeen.HasValue) card.Data.LastSeen = lastSeen;
            card.Refresh();
        });
    }

    private void OnScriptUpdated(string pcKey, string scriptName, bool isActive)
    {
        // Script-level live status is shown in the details window; nothing to do
        // on the card list itself for now.
    }

    private void OnPcDeleted(string pcKey)
    {
        OnUi(() =>
        {
            var card = PcCards.FirstOrDefault(c => c.Data.PcKey == pcKey);
            if (card != null) PcCards.Remove(card);
        });
    }

    private void OnDisconnected(string reason)
    {
        OnUi(() => StatusMessage = $"Disconnected: {reason}");
    }

    [RelayCommand]
    private async Task RefreshAsync() => await _ws.RefreshAsync();

    [RelayCommand]
    private async Task RestartAllScripts(PcCardViewModel card)
    {
        try
        {
            var scripts = await _ws.GetScriptsAsync(card.Data.PcKey);
            foreach (var script in scripts)
            {
                await _ws.SendCommandAsync(card.Data.PcKey, "RESTART_SCRIPT",
                    new { script_name = script.ScriptName });
            }
            MessageBox.Show($"Restart commands sent for all scripts on {card.PcName}.",
                "Commands Sent", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ViewDetails(PcCardViewModel card)
    {
        try
        {
            var detailsVm = new PcDetailsViewModel();
            await detailsVm.LoadAsync(card.Data, _ws);

            var window = new PcDetailsWindow { DataContext = detailsVm };
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading details: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task DeletePc(PcCardViewModel card)
    {
        var result = MessageBox.Show($"Delete PC '{card.PcName}' and all its data?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            await _ws.DeletePcAsync(card.Data.PcKey);
            // Removal is reflected via the pc_deleted push; remove locally too.
            PcCards.Remove(card);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
