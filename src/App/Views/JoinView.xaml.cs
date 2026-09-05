using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    // T-017 internal drag-reorder state: the item + point captured on left-button-down, promoted to a
    // real DragDrop.DoDragDrop only once the pointer moves past the system drag threshold.
    private JoinItemViewModel? _dragItem;
    private Point _dragStart;

    /// <summary>The custom clipboard format carrying the internal item — deliberately NOT FileDrop, so the
    /// existing T-016 external-file Drop handler (on the root grid) never mistakes a reorder for a file drop.</summary>
    private static readonly System.Type InternalItemFormat = typeof(JoinItemViewModel);

    public JoinView()
    {
        InitializeComponent();
    }

    // ---- Copyable error surface (T-037) ----------------------------------------------------

    private void OnCopyErrorClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is JoinViewModel vm)
        {
            ErrorActions.CopyError(vm.Operation.Error);
        }
    }

    private void OnOpenLogClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is JoinViewModel vm)
        {
            ErrorActions.OpenLog(vm.Operation.Error);
        }
    }

    // ---- Internal drag-to-reorder (T-017) --------------------------------------------------

    private void OnClipListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Record the candidate row + start point; the drag itself only begins on PreviewMouseMove
        // once the pointer travels past the system threshold (so clicks / button presses still work).
        _dragItem = ItemUnder(e.OriginalSource as DependencyObject);
        _dragStart = e.GetPosition(null);
    }

    private void OnClipListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        // < 2 items → dragging is a no-op.
        if (ClipList.Items.Count < 2)
        {
            _dragItem = null;
            return;
        }

        var pos = e.GetPosition(null);
        if (System.Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && System.Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return; // not moved far enough yet
        }

        var item = _dragItem;
        _dragItem = null;

        var data = new DataObject(InternalItemFormat, item);
        DragDrop.DoDragDrop(ClipList, data, DragDropEffects.Move);
    }

    private void OnClipListDrop(object sender, DragEventArgs e)
    {
        // External file drops are NOT our business — let them bubble to the root grid's OnDrop (T-016).
        if (!e.Data.GetDataPresent(InternalItemFormat))
        {
            return; // e.Handled stays false → bubbles up to the T-016 FileDrop handler
        }

        if (DataContext is not JoinViewModel vm
            || e.Data.GetData(InternalItemFormat) is not JoinItemViewModel dragged)
        {
            return;
        }

        e.Handled = true; // an internal reorder is fully handled here; do not bubble to the file handler

        var from = ClipList.Items.IndexOf(dragged);
        if (from < 0)
        {
            return;
        }

        // Target = the item under the cursor; a drop onto empty space lands at the end.
        var overItem = ItemUnder(e.OriginalSource as DependencyObject);
        var to = overItem is null ? ClipList.Items.Count - 1 : ClipList.Items.IndexOf(overItem);

        vm.Move(from, to);
    }

    /// <summary>Walk up the visual tree from the hit element to the JoinItemViewModel of the row under it
    /// (null if the point is over empty list space).</summary>
    private JoinItemViewModel? ItemUnder(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        return (source as ListBoxItem)?.DataContext as JoinItemViewModel;
    }

    /// <summary>
    /// Open the Join multiselect file picker and add the chosen clips (T-088). Public so the shared
    /// tab-strip Load button on the MainWindow can drive the active screen's picker without a duplicate
    /// dialog (the old in-panel "Add files…" button was removed). Same behavior the removed
    /// <c>OnAddFilesClicked</c> had.
    /// </summary>
    public void ShowAddFilesPicker()
    {
        if (DataContext is not JoinViewModel vm)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select videos to join",
            // T-158: derived from VideoFileFilter, never hand-typed - the two doors into this screen
            // must offer and accept the same set.
            Filter = VideoFileFilter.DialogFilter,
            CheckFileExists = true,
            Multiselect = true,
        };

        // Open the picker in the folder the user last added files from (T-038), when it still exists.
        var lastInputDir = vm.Settings.LastInputDir;
        if (!string.IsNullOrWhiteSpace(lastInputDir) && System.IO.Directory.Exists(lastInputDir))
        {
            dialog.InitialDirectory = lastInputDir;
        }

        if (dialog.ShowDialog() == true)
        {
            // T-158: same counting entry point as the drop path — see SplitView for why.
            _ = vm.AddDroppedFilesAsync(dialog.FileNames);
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

        DropDiagnostics.Record("over", "Join", TryPaths(e), e.Effects != DragDropEffects.None);
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
        var videos = HandleDroppedFiles(paths, vm);

        // T-154 — `accepted` used to be hard-coded `true` on every drop, including one where the filter
        // refused everything. That is a lie in the one artifact the reporter is asked to paste into a
        // bug report, and it defeats the log's whole purpose (telling "we never saw the drag" apart from
        // "we saw it and refused it"). The note carries the same sentence the screen is showing.
        DropDiagnostics.Record("drop", "Join", paths, accepted: videos.Count > 0, note: vm.DropSummary);
    }

    /// <summary>
    /// Drop routing extracted for testability: hands the RAW dropped paths to the VM, which adds every
    /// video (order preserved, compat re-check follows) and accounts for everything it could not add
    /// (T-154). Returns the video paths that were added.
    ///
    /// <para>It deliberately no longer filters before calling the VM. Filtering here and passing only
    /// the survivors is exactly why Join could not report a refusal: the VM was never told anything had
    /// been dropped that it did not receive.</para>
    /// </summary>
    internal static System.Collections.Generic.IReadOnlyList<string> HandleDroppedFiles(
        string[] paths, JoinViewModel vm)
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
