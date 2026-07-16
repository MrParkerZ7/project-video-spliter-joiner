using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VideoSplitJoiner.App.ViewModels;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// Code-behind for the Join screen. Keeps only the file-dialog + drag-drop plumbing here
/// (GUI concerns); all join logic lives in <see cref="JoinViewModel"/>. The "Add files…"
/// button opens a multiselect <see cref="OpenFileDialog"/> and forwards the chosen paths to
/// the VM's <c>AddFilesCommand</c>; dropped video files are routed to the same command (T-016).
/// </summary>
public partial class JoinView : UserControl
{
    public JoinView()
    {
        InitializeComponent();
    }

    private void OnAddFilesClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not JoinViewModel vm)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select videos to join",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts|All files|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog() == true)
        {
            vm.AddFilesCommand.Execute(dialog.FileNames);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // Only accept EXTERNAL file drops. A future internal item-reorder drag (T-017) uses a
        // different payload and must not be hijacked here.
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

        if (DataContext is not JoinViewModel vm)
        {
            return;
        }

        // Only handle external FileDrop payloads; ignore non-FileDrop so a future internal
        // item-reorder drag (T-017) is not intercepted.
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        e.Handled = true;
        HandleDroppedFiles(paths, vm);
    }

    /// <summary>
    /// Drop routing extracted for testability: filters the paths to videos (order preserved)
    /// and, if any, adds ALL of them via the existing <see cref="JoinViewModel.AddFilesCommand"/>
    /// (the VM's compat re-check then runs). Returns the filtered video paths that were added.
    /// Empty-after-filter is a no-op.
    /// </summary>
    internal static System.Collections.Generic.IReadOnlyList<string> HandleDroppedFiles(
        string[] paths, JoinViewModel vm)
    {
        var videos = VideoFileFilter.AcceptVideoFiles(paths);
        if (videos.Count > 0)
        {
            vm.AddFilesCommand.Execute(videos.ToArray());
        }

        return videos;
    }
}
