using System.Windows;
using MesControlCenter.Core.Models;
using MesControlCenter.UI.ViewModels;

namespace MesControlCenter.UI.Views;

public partial class ScriptEditorWindow : Window
{
    public ScriptEditorWindow(ScriptEntry? existing = null, IEnumerable<string>? availableFolders = null)
    {
        InitializeComponent();
        var vm = new ScriptEditorViewModel();
        if (availableFolders != null) vm.SetAvailableFolders(availableFolders);
        if (existing != null) vm.LoadFrom(existing);
        DataContext = vm;
    }

    private void BrowseScript_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Scripts and Executables (*.py;*.exe;*.bat;*.cmd;*.ps1)|*.py;*.exe;*.bat;*.cmd;*.ps1|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true && DataContext is ScriptEditorViewModel vm)
            vm.ScriptPath = dlg.FileName;
    }

    private void BrowseWorkDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() == true && DataContext is ScriptEditorViewModel vm)
            vm.WorkDir = dlg.FolderName;
    }

    private void BrowseGitRepoDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() == true && DataContext is ScriptEditorViewModel vm)
            vm.GitRepoDir = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ScriptEditorViewModel vm)
        {
            var entry = vm.ToScriptEntry();
            if (entry != null)
                DialogResult = true;
        }
    }
}
