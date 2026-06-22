using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MesControlCenter.UI.ViewModels;

namespace MesControlCenter.UI;

public partial class MainWindow : Window
{
    private const double LogScrollBottomTolerance = 6;
    private bool _autoScrollToEnd = true;
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            _viewModel = vm;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            vm.Initialize();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedScript)) return;

        _autoScrollToEnd = true;
        Dispatcher.BeginInvoke(new Action(() => LogOutput.ScrollToEnd()), DispatcherPriority.Background);
    }

    // ponytail: autoscroll only on text change, only when user is already at the
    // bottom. No ScrollChanged handler — that fed ScrollToEnd back into itself and
    // pinned the UI thread at 100% CPU while a script streamed output.
    private void LogOutput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb && _autoScrollToEnd)
            tb.ScrollToEnd();
    }

    private void LogOutput_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Only react to user scrolling (offset change), not to content growth.
        if (sender is TextBox tb && e.ViewportHeightChange == 0 && e.ExtentHeightChange == 0)
            _autoScrollToEnd = tb.VerticalOffset + tb.ViewportHeight >= tb.ExtentHeight - LogScrollBottomTolerance;
    }

    // ═══════ Folder tree: selection, context menu, drag & drop ═══════

    private Point _dragStart;
    private object? _dragItem;
    private Helpers.FolderNodeViewModel? _lastDropTarget;

    private void ScriptTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_viewModel != null && e.NewValue is Helpers.ScriptEntryViewModel script)
            _viewModel.SelectedScript = script;
    }

    private void NewRootFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptName("Nueva carpeta", "Nombre de la carpeta:", "");
        if (!string.IsNullOrWhiteSpace(name))
            _viewModel?.CreateFolder(null, name);
    }

    private static Helpers.FolderNodeViewModel? FolderFromMenu(object sender)
        => (sender as MenuItem)?.Tag as Helpers.FolderNodeViewModel;

    private void MenuNewSubfolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderFromMenu(sender) is not { } folder) return;
        var n = PromptName("Nueva subcarpeta", $"Subcarpeta de '{folder.Name}':", "");
        if (!string.IsNullOrWhiteSpace(n)) _viewModel?.CreateFolder(folder.Path, n);
    }

    private void MenuRenameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderFromMenu(sender) is not { } folder) return;
        var n = PromptName("Renombrar carpeta", "Nuevo nombre:", folder.Name);
        if (!string.IsNullOrWhiteSpace(n)) _viewModel?.RenameFolder(folder.Path, n);
    }

    private void MenuDeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderFromMenu(sender) is not { } folder) return;
        var r = MessageBox.Show(
            $"¿Eliminar la carpeta '{folder.Name}'?\nSus scripts y subcarpetas se moverán al nivel superior.",
            "Eliminar carpeta", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r == MessageBoxResult.Yes) _viewModel?.DeleteFolder(folder.Path);
    }

    private void ScriptTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = (e.OriginalSource as DependencyObject).DataContextOfType();
    }

    private void ScriptTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;
        var diff = _dragStart - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        DragDrop.DoDragDrop(ScriptTree, _dragItem, DragDropEffects.Move);
        ClearDropHighlight();
        _dragItem = null;
    }

    private void ScriptTree_Drop(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
        var target = (e.OriginalSource as DependencyObject).DataContextOfType();
        var targetFolder = target as Helpers.FolderNodeViewModel;

        // Dropped data: a script → move to folder (or root if dropped on empty space);
        // a folder → re-parent under target (or root).
        if (e.Data.GetData(typeof(Helpers.ScriptEntryViewModel)) is Helpers.ScriptEntryViewModel script)
            _viewModel?.MoveScriptToFolder(script.Id, targetFolder?.Path);
        else if (e.Data.GetData(typeof(Helpers.FolderNodeViewModel)) is Helpers.FolderNodeViewModel folder
                 && folder != targetFolder)
            _viewModel?.MoveFolder(folder.Path, targetFolder?.Path);
    }

    // Highlight the folder under the cursor while dragging.
    private void ScriptTree_DragOver(object sender, DragEventArgs e)
    {
        var folder = (e.OriginalSource as DependencyObject).DataContextOfType() as Helpers.FolderNodeViewModel;
        if (!ReferenceEquals(folder, _lastDropTarget))
        {
            ClearDropHighlight();
            if (folder != null) { folder.IsDropTarget = true; _lastDropTarget = folder; }
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ClearDropHighlight()
    {
        if (_lastDropTarget != null) { _lastDropTarget.IsDropTarget = false; _lastDropTarget = null; }
    }

    // Minimal modal name prompt (WPF has none built-in). Uses the app's styles so
    // it matches the dark theme instead of rendering white system controls.
    private string? PromptName(string title, string label, string initial)
    {
        var darkBtn = TryFindResource("DarkButton") as Style;
        var accentBtn = TryFindResource("AccentButton") as Style;

        var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 14), Padding = new Thickness(8, 6, 8, 6), FontSize = 14 };
        var ok = new Button { Content = "Aceptar", Width = 100, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6), Style = accentBtn };
        var cancel = new Button { Content = "Cancelar", Width = 100, IsCancel = true, Padding = new Thickness(12, 6, 12, 6), Style = darkBtn };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Foreground });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dlg = new Window
        {
            Title = title, Content = panel, Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            ResizeMode = ResizeMode.NoResize, Background = Background, Foreground = Foreground,
            Resources = this.Resources
        };
        ok.Click += (_, _) => { dlg.DialogResult = true; };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dlg.ShowDialog() == true ? box.Text.Trim() : null;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

        base.OnClosed(e);
    }
}

internal static class TreeDragExtensions
{
    // Walks up the visual/logical tree from the hit element to the first item's DataContext.
    public static object? DataContextOfType(this DependencyObject? src)
    {
        while (src != null)
        {
            if (src is FrameworkElement fe &&
                (fe.DataContext is Helpers.FolderNodeViewModel || fe.DataContext is Helpers.ScriptEntryViewModel))
                return fe.DataContext;
            src = System.Windows.Media.VisualTreeHelper.GetParent(src);
        }
        return null;
    }
}
