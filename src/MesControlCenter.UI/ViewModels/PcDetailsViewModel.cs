using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MesControlCenter.Core.Models;
using MesControlCenter.Core.Services;

namespace MesControlCenter.UI.ViewModels;

public partial class PcDetailsViewModel : ObservableObject
{
    [ObservableProperty] private string _pcName = string.Empty;
    [ObservableProperty] private string _pcKey = string.Empty;
    [ObservableProperty] private string _role = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private SolidColorBrush _statusColor = new(Colors.Gray);
    [ObservableProperty] private string _lastSeen = string.Empty;

    public ObservableCollection<PcScript> Scripts { get; } = new();

    public async Task LoadAsync(PcInfo pc, WsDashboardClient ws)
    {
        PcName = pc.PcName;
        PcKey = pc.PcKey;
        Role = pc.Role;
        LastSeen = pc.LastSeen?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

        bool active = pc.IsActive && (pc.SecondsSinceSeen ?? int.MaxValue) < 120;
        StatusText = active ? "ACTIVO" : "INACTIVO";
        StatusColor = new SolidColorBrush(active ? (Color)ColorConverter.ConvertFromString("#10b981")
                                                 : (Color)ColorConverter.ConvertFromString("#ef4444"));

        var scripts = await ws.GetScriptsAsync(pc.PcKey);
        Scripts.Clear();
        foreach (var s in scripts)
            Scripts.Add(s);
    }
}
