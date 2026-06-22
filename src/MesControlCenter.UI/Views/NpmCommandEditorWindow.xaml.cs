using System.Windows;
using MesControlCenter.Core.Models;
using MesControlCenter.UI.ViewModels;

namespace MesControlCenter.UI.Views;

public partial class NpmCommandEditorWindow : Window
{
    public NpmCommandEditorWindow(ScriptEntry? existing = null, IEnumerable<string>? availableFolders = null)
    {
        InitializeComponent();
        var vm = new NpmCommandEditorViewModel();
        if (availableFolders != null) vm.SetAvailableFolders(availableFolders);
        if (existing != null) vm.LoadFrom(existing);
        DataContext = vm;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is NpmCommandEditorViewModel vm)
        {
            var entry = vm.ToScriptEntry();
            if (entry != null)
                DialogResult = true;
        }
    }
}
