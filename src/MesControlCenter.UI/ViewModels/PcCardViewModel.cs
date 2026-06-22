using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MesControlCenter.Core.Models;

namespace MesControlCenter.UI.ViewModels;

public partial class PcCardViewModel : ObservableObject
{
    public PcInfo Data { get; }

    public PcCardViewModel(PcInfo data)
    {
        Data = data;
    }

    public string PcName => Data.PcName;
    public string Role => Data.Role;

    public bool IsActive => Data.IsActive && (Data.SecondsSinceSeen ?? int.MaxValue) < 120;

    public string StatusText => IsActive ? "ACTIVO" : "INACTIVO";

    public SolidColorBrush StatusBorderBrush => new(
        IsActive ? (Color)ColorConverter.ConvertFromString("#10b981")
                 : (Color)ColorConverter.ConvertFromString("#ef4444"));

    public string LastSeenText
    {
        get
        {
            var seconds = Data.SecondsSinceSeen;
            if (seconds == null) return "Never";
            if (seconds < 60) return $"Seen {seconds}s ago";
            if (seconds < 3600) return $"Seen {seconds / 60}m ago";
            return $"Seen {seconds / 3600}h ago";
        }
    }

    /// <summary>Re-raises change notifications for the computed properties after
    /// the underlying <see cref="Data"/> was mutated by a live push update.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBorderBrush));
        OnPropertyChanged(nameof(LastSeenText));
        OnPropertyChanged(nameof(PcName));
        OnPropertyChanged(nameof(Role));
    }
}
