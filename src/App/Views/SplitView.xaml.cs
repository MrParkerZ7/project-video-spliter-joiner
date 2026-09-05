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

        // T-162/T-163: supply the real confirmations for the destructive source-deletion path. Both
        // view-model gates default to REFUSING, so forgetting this wiring can never silently destroy a
        // user's file — it can only leave the feature inert.
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is SplitViewModel vm)
            {
                vm.ConfirmDeleteOriginal = ConfirmDeleteOriginal;
                vm.ConfirmPermanentDeletion = ConfirmPermanentDeletion;
                vm.EmptyRecycleBinAction = () => VideoSplitJoiner.App.Io.ShellRecycleBin.EmptyBin();
            }
        };
    }

    /// <summary>
    /// T-162 — the delete-source gate. Names the file and the space reclaimed (that is the point of the
    /// feature), says plainly that it goes to the Recycle Bin, and defaults to No so a stray Enter does
    /// not remove someone's footage.
    /// </summary>
    private static bool ConfirmDeleteOriginal(string name, long bytes)
    {
        var size = bytes >= 1024L * 1024 * 1024
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#} GB", bytes / 1024d / 1024d / 1024d)
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} MB", System.Math.Max(1, bytes / 1024 / 1024));

        var result = MessageBox.Show(
            $"Send \"{name}\" to the Recycle Bin, freeing about {size}?" + System.Environment.NewLine + System.Environment.NewLine +
            "Every part this split produced has been checked and is on disk. Nothing is deleted " +
            "permanently — you can restore the source from the Recycle Bin.",
            "Delete the original?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    /// <summary>
    /// T-163 — asked ONCE, before permanent deletion is armed. It must say that emptying the bin is not
    /// scoped to this app: SHEmptyRecycleBin empties the WHOLE bin, including files other programs put
    /// there. Defaults to No.
    /// </summary>
    private static bool ConfirmPermanentDeletion()
    {
        var result = MessageBox.Show(
            "The source file will be deleted PERMANENTLY after a successful split." + System.Environment.NewLine +
            "It cannot be restored." + System.Environment.NewLine + System.Environment.NewLine +
            "This also empties the whole Recycle Bin, including files deleted by other programs." +
            System.Environment.NewLine + System.Environment.NewLine +
            "Turn this on?",
            "Delete the source permanently?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
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

        var videos = HandleDroppedFiles(paths, vm);

        // T-154 — `accepted` used to be hard-coded `true` on every drop, including one where the filter
        // refused everything. That is a lie in the one artifact the reporter is asked to paste into a
        // bug report, and it defeats the log's whole purpose (telling "we never saw the drag" apart from
        // "we saw it and refused it"). The note carries the same sentence the screen is showing.
        DropDiagnostics.Record("drop", "Split", paths, accepted: videos.Count > 0, note: vm.DropSummary);
    }

    /// <summary>
    /// Pure-ish drop routing extracted for testability: hands the RAW dropped paths to the VM, which
    /// loads the first video and accounts for everything it could not load (T-154). Returns the video
    /// paths the drop contained — Split loads only the first of them.
    ///
    /// <para>It deliberately no longer filters before calling the VM. Filtering here and passing only
    /// the survivors is exactly why Split could not report a refusal: the VM was never told anything had
    /// been dropped that it did not receive.</para>
    /// </summary>
    internal static System.Collections.Generic.IReadOnlyList<string> HandleDroppedFiles(
        string[] paths, SplitViewModel vm)
    {
        var videos = VideoFileFilter.AcceptVideoFiles(paths);
        _ = vm.AddDroppedFilesAsync(paths);
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
