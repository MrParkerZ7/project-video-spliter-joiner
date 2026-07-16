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

    private void OnLoadClicked(object sender, RoutedEventArgs e)
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
}
