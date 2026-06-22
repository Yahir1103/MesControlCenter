using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MesControlCenter.UI.Helpers;

/// <summary>
/// A folder in the script tree. Path is the full "/"-joined route (e.g.
/// "ALMACEN/BACKEND"); Name is the last segment. Children mixes sub-folders
/// (FolderNodeViewModel) and scripts (ScriptEntryViewModel) in one collection
/// so the TreeView can render both via type-matched templates.
/// </summary>
public partial class FolderNodeViewModel : ObservableObject
{
    public FolderNodeViewModel(string path)
    {
        Path = path;
        var slash = path.LastIndexOf('/');
        Name = slash < 0 ? path : path[(slash + 1)..];
    }

    public string Path { get; }
    public string Name { get; }

    public ObservableCollection<object> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isDropTarget;

    public int ScriptCount => Children.OfType<ScriptEntryViewModel>().Count()
                            + Children.OfType<FolderNodeViewModel>().Sum(f => f.ScriptCount);
}
