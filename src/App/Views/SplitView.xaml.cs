using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VideoSplitJoiner.App.ViewModels;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// Code-behind for the Split screen. Keeps only the file-dialog + drag-drop plumbing here
/// (GUI concerns); all split logic lives in <see cref="SplitViewModel"/>. The "Load…" button
/// opens an <see cref="OpenFileDialog"/> and forwards the chosen path to the VM's
/// <c>LoadCommand</c>; dropped video files are routed to the same command (T-016).
/// </summary>
public partial class SplitView : UserControl
{
    public SplitView()
    {
        InitializeComponent();
    }

    private void OnCopyErrorClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is SplitViewModel vm)
        {
            ErrorActions.CopyError(vm.Operation.Error);
        }
    }

    private void OnOpenLogClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is SplitViewModel vm)
        {
            ErrorActions.OpenLog(vm.Operation.Error);
        }
    }

    /// <summary>
    /// Open the Split file picker and load the chosen file (T-088). Public so the shared tab-strip
    /// Load button on the MainWindow can drive the active screen's picker without a duplicate dialog
    /// (the old in-panel Load button was removed). Same behavior the removed <c>OnLoadClicked</c> had.
    /// </summary>
    public void ShowLoadPicker()
    {
        if (DataContext is not SplitViewModel vm)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select a video to split",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts|All files|*.*",
            CheckFileExists = true,
        };

        // Open the picker in the folder the user last chose an input from (T-038), when it still exists.
        var lastInputDir = vm.Settings.LastInputDir;
        if (!string.IsNullOrWhiteSpace(lastInputDir) && System.IO.Directory.Exists(lastInputDir))
        {
            dialog.InitialDirectory = lastInputDir;
        }

        if (dialog.ShowDialog() == true)
        {
            vm.LoadCommand.Execute(dialog.FileName);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths
            && VideoFileFilter.HasAnyVideo(paths))
        {
            e.Effects = DragDropEffects.Copy;
            DropHighlight.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            DropHighlight.Visibility = Visibility.Collapsed;
        }

        DropDiagnostics.Record("over", "Split", TryPaths(e), e.Effects != DragDropEffects.None);
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (DataContext is not SplitViewModel vm)
        {
            return;
        }

        // Only handle external file drops; ignore other payloads.
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        DropDiagnostics.Record("drop", "Split", paths, accepted: true);
        HandleDroppedFiles(paths, vm);
    }

    /// <summary>
    /// Pure-ish drop routing extracted for testability: filters the paths to videos and,
    /// if any, loads the FIRST one via the existing <see cref="SplitViewModel.LoadCommand"/>
    /// (Split loads a single file). Returns the filtered video paths that were considered.
    /// Empty-after-filter is a no-op.
    /// </summary>
    internal static System.Collections.Generic.IReadOnlyList<string> HandleDroppedFiles(
        string[] paths, SplitViewModel vm)
    {
        var videos = VideoFileFilter.AcceptVideoFiles(paths);
        if (videos.Count > 0)
        {
            vm.LoadCommand.Execute(videos[0]);
        }

        return videos;
    }

    /// <summary>T-154 — the dropped paths, or null. Never throws; a diagnostic must not break the drop.</summary>
    private static string[]? TryPaths(DragEventArgs e)
    {
        try
        {
            return e.Data.GetDataPresent(DataFormats.FileDrop)
                ? e.Data.GetData(DataFormats.FileDrop) as string[]
                : null;
        }
        catch
        {
            return null;
        }
    }
}
