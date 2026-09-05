using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using VideoSplitJoiner.App.ViewModels;
using VideoSplitJoiner.Core.Bulk;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// Code-behind for the Bulk Cut screen (D-004 / T-097). Keeps only the GUI plumbing here — the
/// file-dialog + drag-drop (mirroring <see cref="JoinView"/>), the per-row outro / time-edit gestures
/// that route to the WPF-free <see cref="BulkItemViewModel"/>, the aggregate + per-row error/folder OS
/// glue (via <see cref="ErrorActions"/>), and the failed-row jump. All batch logic lives in
/// <see cref="BulkCutViewModel"/>; the dual-handle scrub bar is the separate view-only
/// <see cref="BulkRowScrubView"/>.
/// </summary>
public partial class BulkCutView : UserControl
{
    public BulkCutView()
    {
        InitializeComponent();

        // T-123: supply the real confirmation for the destructive replace-originals run. The VM defaults
        // to REFUSING, so forgetting this wiring can never silently destroy a user's masters.
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is BulkCutViewModel vm)
            {
                vm.ConfirmReplaceOriginals = ConfirmReplaceOriginals;
                vm.ConfirmDeleteOriginals = ConfirmDeleteOriginals;

                // T-147: the profile backup/restore file pickers and the overwrite gate. The VM defaults
                // to KEEPING existing profiles on a collision, so a missing wiring can never silently
                // overwrite someone's profiles.
                vm.ConfirmPermanentDeletion = ConfirmPermanentDeletion;
                vm.EmptyRecycleBinAction = () => VideoSplitJoiner.App.Io.ShellRecycleBin.EmptyBin();
                vm.ChooseProfileExportPath = ChooseProfileExportPath;
                vm.ChooseProfileImportPath = ChooseProfileImportPath;
                vm.ConfirmProfileOverwrite = ConfirmProfileOverwrite;
            }
        };
    }

    /// <summary>
    /// Block for an explicit, COUNTED confirmation before a batch replaces originals (T-123). Defaults
    /// to No, so an accidental Enter/Escape does not destroy anything.
    /// </summary>
    /// <summary>
    /// T-144 - the delete-originals gate. Names the count AND the space reclaimed (that is the point of
    /// the feature), says plainly that the files go to the Recycle Bin, and defaults to No.
    /// </summary>
    private static bool ConfirmDeleteOriginals(int count, long bytes)
    {
        var noun = count == 1 ? "original" : "originals";
        var size = bytes >= 1024L * 1024 * 1024
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.#} GB", bytes / 1024d / 1024d / 1024d)
            : string.Format(CultureInfo.InvariantCulture, "{0} MB", Math.Max(1, bytes / 1024 / 1024));

        var result = MessageBox.Show(
            $"Send {count} {noun} to the Recycle Bin, freeing about {size}?" + Environment.NewLine + Environment.NewLine +
            "Only videos that were trimmed successfully and whose trimmed file is still on disk are " +
            "included. Nothing is deleted permanently - you can restore them from the Recycle Bin.",
            "Delete originals?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    /// <summary>T-147 - where to write the profile backup. Null cancels.</summary>
    private static string? ChooseProfileExportPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Back up cut profiles",
            Filter = "Profile backup|*.json|All files|*.*",
            DefaultExt = ".json",
            FileName = "cut-profiles-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json",
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>T-147 - which backup to restore. Null cancels.</summary>
    private static string? ChooseProfileImportPath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Restore cut profiles",
            Filter = "Profile backup|*.json|All files|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>
    /// T-147 - a restore must never quietly cost someone the profiles they already had. Names the count,
    /// spells out both outcomes, and defaults to KEEPING what is already there.
    /// </summary>
    private static bool ConfirmProfileOverwrite(int count)
    {
        var noun = count == 1 ? "profile" : "profiles";
        var result = MessageBox.Show(
            $"This backup contains {count} {noun} with the same name as one you already have." +
            Environment.NewLine + Environment.NewLine +
            "Yes - overwrite them with the backup's version." + Environment.NewLine +
            "No - keep yours, and import only the profiles that are new.",
            "Overwrite existing profiles?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    /// <summary>
    /// T-156 — asked ONCE, before permanent deletion is armed. The VM defaults to refusing, so a missing
    /// wiring can never turn this on by itself.
    /// </summary>
    private static bool ConfirmPermanentDeletion()
    {
        var result = MessageBox.Show(
            "Originals will be deleted PERMANENTLY after each successful batch." + Environment.NewLine +
            "They cannot be restored." + Environment.NewLine + Environment.NewLine +
            "This also empties the whole Recycle Bin, including files deleted by other programs." +
            Environment.NewLine + Environment.NewLine +
            "Turn this on?",
            "Delete originals permanently?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    private static bool ConfirmReplaceOriginals(int count)
    {
        var noun = count == 1 ? "file" : "files";
        var result = MessageBox.Show(
            $"This will REPLACE {count} original {noun} with the trimmed result." + Environment.NewLine + Environment.NewLine +
            "Each replaced original is sent to the Recycle Bin, so you can restore it if something " +
            "looks wrong. Continue?",
            "Replace originals?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    // ---- Add-files picker (mirror JoinView.ShowAddFilesPicker) -------------------------------

    /// <summary>
    /// Open the Bulk multiselect file picker and add the chosen videos. Public so the shared tab-strip
    /// Load button on the MainWindow can drive this screen's picker (T-088 placement).
    /// </summary>
    public void ShowAddFilesPicker()
    {
        if (DataContext is not BulkCutViewModel vm)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select videos to bulk-trim",
            // T-158: derived from VideoFileFilter, never hand-typed - the two doors into this screen
            // must offer and accept the same set.
            Filter = VideoFileFilter.DialogFilter,
            CheckFileExists = true,
            Multiselect = true,
        };

        var lastInputDir = vm.Settings.LastInputDir;
        if (!string.IsNullOrWhiteSpace(lastInputDir) && Directory.Exists(lastInputDir))
        {
            dialog.InitialDirectory = lastInputDir;
        }

        if (dialog.ShowDialog() == true)
        {
            // T-158: same counting entry point as the drop path — see SplitView for why.
            _ = vm.AddDroppedFilesAsync(dialog.FileNames);
        }
    }

    // ---- Drag-drop (mirror JoinView) --------------------------------------------------------

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

        DropDiagnostics.Record("over", "BulkCut", TryPaths(e), e.Effects != DragDropEffects.None);
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        e.Handled = true;
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

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;

        if (DataContext is not BulkCutViewModel vm)
        {
            return;
        }

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
        DropDiagnostics.Record("drop", "BulkCut", paths, accepted: videos.Count > 0, note: vm.DropSummary);
    }

    /// <summary>
    /// Drop routing extracted for testability: filters the paths to videos (order preserved) and, if
    /// any, adds ALL of them via <see cref="BulkCutViewModel.AddFilesCommand"/>. Returns the filtered
    /// video paths; empty-after-filter is a no-op.
    /// </summary>
    internal static IReadOnlyList<string> HandleDroppedFiles(string[] paths, BulkCutViewModel vm)
    {
        // T-154: routes through the VM so a file that is NOT added is accounted for. The old version
        // filtered silently here and told the VM only about the survivors, so the VM had no idea anything
        // had been refused and nothing could report it.
        var videos = VideoFileFilter.AcceptVideoFiles(paths);
        _ = vm.AddDroppedFilesAsync(paths);
        return videos;
    }

    // ---- Outro add / clear (row methods) ----------------------------------------------------

    private void OnAddOutroClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BulkItemViewModel row)
        {
            return;
        }

        // Place the outro-start optimistically near the tail (last ~10%, capped 60s) so the user can
        // then drag it; if the duration isn't known yet, drop it at zero and let the drag reposition it.
        var at = row.Duration is { } d && d.TotalSeconds > 0
            ? TimeSpan.FromSeconds(Math.Max(0, d.TotalSeconds - Math.Min(60, d.TotalSeconds * 0.1)))
            : TimeSpan.Zero;

        row.AddOutro(at);
    }

    private void OnClearOutroClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BulkItemViewModel row)
        {
            row.ClearOutro();
        }
    }

    // ---- Editable IN/OUT time commit (parse mm:ss.f → set Requested → snap) ------------------

    private void OnCutTimeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            CommitCutTime(box);
            e.Handled = true;
        }
    }

    private void OnCutTimeLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            CommitCutTime(box);
        }
    }

    private static void CommitCutTime(TextBox box)
    {
        if (box.DataContext is not BulkItemViewModel row)
        {
            return;
        }

        var isIntro = (box.Tag as string) == "intro";
        var marker = isIntro ? row.IntroEnd : row.OutroStart;
        if (marker is null)
        {
            return;
        }

        // T-118: the field renders a VM-produced time (the keyframe-SNAPPED value, truncated to 0.1s).
        // Committing that back on an untouched focus pass would overwrite the user's real Requested with
        // the snapped value - destroying the request, zeroing the snap delta, and sending the truncated
        // time to the engine. Only a genuine, parseable user edit is committed.
        if (CutTimeCommit.TryResolveEdit(box.Text, marker.Requested, out var t))
        {
            marker.Requested = t;
        }

        // Normalize the field back to the VM truth (also reverts an unparseable entry).
        BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateTarget();
    }

    /// <summary>Parse <c>mm:ss.f</c> / <c>h:mm:ss.f</c> / plain seconds into a non-negative TimeSpan.</summary>
    /// <remarks>T-118: the real implementation lives in the WPF-free <see cref="CutTimeCommit"/> so it is unit-testable.</remarks>
    internal static bool TryParseClock(string? text, out TimeSpan value)
        => CutTimeCommit.TryParseClock(text, out value);

    // ---- Cut-profile save: themed inline name popup (T-103) ---------------------------------

    /// <summary>
    /// Open the themed inline name input under the Save button (not a raw WPF dialog). Pre-fills with the
    /// currently-selected profile's name (handy for a "save over" upsert) and focuses/selects the field.
    /// </summary>
    /// <summary>
    /// Close the save popup, releasing anything it is holding on to (T-138).
    ///
    /// <para><b>Why this is not just <c>IsOpen = false</c>.</b> A <c>Popup</c> with
    /// <c>StaysOpen="False"</c> takes MOUSE CAPTURE so it can notice a click outside itself and dismiss.
    /// When such a popup is closed PROGRAMMATICALLY — which is what pressing Save does — while keyboard
    /// focus sits in a control inside it (<c>ProfileNameBox</c>), that capture can be left dangling: the
    /// popup is gone, but the window keeps routing mouse input to a target that no longer exists, so the
    /// app appears frozen until some other interaction forces the capture loose. That is the reported
    /// "UI gone broke until I click X".</para>
    ///
    /// <para>Releasing capture and moving focus back to the button that opened it is correct on every
    /// close path regardless — a closed popup should hold neither.</para>
    /// </summary>
    private void CloseSaveProfilePopup()
    {
        SaveProfilePopup.IsOpen = false;

        // Order matters: drop capture first, then give focus somewhere real in the main window.
        if (Mouse.Captured is not null)
        {
            Mouse.Capture(null);
        }

        SaveProfileButton.Focus();
    }

    private void OnSaveProfileClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BulkCutViewModel vm || vm.SelectedItem is null)
        {
            return;
        }

        ProfileNameBox.Text = vm.SelectedProfile?.Name ?? string.Empty;
        SaveProfilePopup.IsOpen = true;
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
    }

    private void OnProfileNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSaveProfile();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSaveProfilePopup();
            e.Handled = true;
        }
    }

    private void OnConfirmSaveProfileClicked(object sender, RoutedEventArgs e) => CommitSaveProfile();

    private void OnCancelSaveProfileClicked(object sender, RoutedEventArgs e) => CloseSaveProfilePopup();

    // ---- Profile thumbnail: upload / clear (T-107) ------------------------------------------

    /// <summary>
    /// Pick an image and hand it to the VM to store as the SELECTED profile's thumbnail (T-107). The VM
    /// copies it into the T-106 <c>ProfileThumbnailStore</c>, folds the stored path onto the profile, and
    /// re-persists. The OpenFileDialog lives here (View), keeping the VM WPF-free.
    /// <para>T-129: a CANCELLED picker is still a no-op — the user changed their mind, that is not a
    /// failure — but a chosen file that cannot be stored is no longer swallowed:
    /// <see cref="BulkCutViewModel.UploadThumbnail"/> returns false and reports the reason on the screen's
    /// existing error surface (<c>Operation.Error</c> — headline + hint + Copy details), which this
    /// screen already renders. No MessageBox: the failure belongs on the same surface as every other Bulk
    /// Cut failure. The early return below is the no-selected-profile case, which the button's
    /// <c>IsEnabled="{Binding HasSelectedProfile}"</c> already prevents.</para>
    /// </summary>
    private void OnUploadThumbnailClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BulkCutViewModel vm || vm.SelectedProfile is not { } profile)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose a thumbnail image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            vm.UploadThumbnail(profile, dialog.FileName);
        }
    }

    /// <summary>Clear the selected profile's thumbnail via the VM (best-effort store delete + null the path).</summary>
    private void OnClearThumbnailClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is BulkCutViewModel vm && vm.SelectedProfile is { } profile)
        {
            vm.ClearThumbnail(profile);
        }
    }

    /// <summary>Validate non-blank, hand the trimmed name to the VM's save command, then close the popup.</summary>
    private void CommitSaveProfile()
    {
        if (DataContext is not BulkCutViewModel vm)
        {
            return;
        }

        var name = ProfileNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ProfileNameBox.Focus(); // keep the popup open until a non-empty name is entered
            return;
        }

        vm.SaveProfileCommand.Execute(name);
        CloseSaveProfilePopup();
    }

    // ---- Completed surface: reveal the output folder ----------------------------------------

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BulkCutViewModel vm)
        {
            return;
        }

        // Reveal the first successfully-trimmed row's output (outputs land beside their sources).
        var done = vm.Items.FirstOrDefault(i => i.RowState == RowState.Done && !string.IsNullOrEmpty(i.OutputPath));
        var path = done?.OutputPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
        }
        catch
        {
            // Opening Explorer is non-critical; never crash the app.
        }
    }

    // ---- Aggregate error surface (Blocked / disk pre-flight) --------------------------------

    private void OnCopyErrorClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is BulkCutViewModel vm)
        {
            ErrorActions.CopyError(vm.Operation.Error);
        }
    }

    private void OnOpenLogClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is BulkCutViewModel vm)
        {
            ErrorActions.OpenLog(vm.Operation.Error);
        }
    }

    // ---- Failed-row list: jump + per-row copy/log -------------------------------------------

    private void OnJumpToFailedClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BulkTrimItemResult result
            || result.Item.Tag is not BulkItemViewModel row)
        {
            return;
        }

        var container = RowsList.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement;
        container?.BringIntoView();
    }

    private void OnCopyFailedErrorClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BulkTrimItemResult result)
        {
            ErrorActions.CopyError(result.Error);
        }
    }

    private void OnOpenFailedLogClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BulkTrimItemResult result)
        {
            ErrorActions.OpenLog(result.Error);
        }
    }
}

// =============================================================================================
// Converters used only by the Bulk Cut view (kept here so the T-097 slice is self-contained).
// =============================================================================================

/// <summary>Formats a <see cref="TimeSpan"/> (or nullable) as <c>mm:ss.f</c> / <c>h:mm:ss.f</c>.</summary>
public sealed class ClockConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var t = value switch
        {
            TimeSpan ts => ts,
            _ => (TimeSpan?)null,
        };
        return t is { } v ? CutMarkerViewModel.FormatClock(v) : string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Reduces a full path to its file name (e.g. <c>ep01_trimmed.mp4</c>).</summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string p && !string.IsNullOrEmpty(p) ? Path.GetFileName(p) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible when the bound count is zero (drives the empty-state overlay); Collapsed otherwise.</summary>
public sealed class EmptyCountToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible when the bound count is &gt; 0 (drives the failed-some surface); Collapsed otherwise.</summary>
public sealed class PositiveCountToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="RowState"/> to a chip <c>label</c> or role <c>brush</c> (parameter selects which),
/// resolving the theme brush from the app resources with a frozen fallback so it renders design-time too.
/// </summary>
public sealed class RowStateConverter : IValueConverter
{
    private static readonly Brush Ok = Frozen("#FF5FCF8E");
    private static readonly Brush Info = Frozen("#FF5B9CF0");
    private static readonly Brush Accent = Frozen("#FFE0A83A");
    private static readonly Brush Danger = Frozen("#FFE5646B");
    private static readonly Brush Muted = Frozen("#FF767C88");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = value is RowState s ? s : RowState.Loading;
        var role = (parameter as string)?.ToLowerInvariant() ?? "label";

        var (label, key) = state switch
        {
            RowState.Loading => ("loading…", "TextMutedBrush"),
            RowState.Ready => ("ready", "OkBrush"),
            RowState.Invalid => ("invalid", "DangerBrush"),
            RowState.NoOpTrim => ("no-op", "TextMutedBrush"),
            RowState.LoadFailed => ("load failed", "DangerBrush"),
            RowState.Queued => ("queued", "InfoBrush"),
            RowState.Running => ("running", "AccentBrush"),
            RowState.Done => ("done ✓", "OkBrush"),
            RowState.Failed => ("failed ✕", "DangerBrush"),
            RowState.Skipped => ("skipped", "TextMutedBrush"),
            RowState.Cancelled => ("cancelled", "TextMutedBrush"),
            _ => (state.ToString().ToLowerInvariant(), "TextMutedBrush"),
        };

        if (role == "brush")
        {
            return ResolveBrush(key);
        }

        return label;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush ResolveBrush(string key)
    {
        if (Application.Current?.TryFindResource(key) is Brush b)
        {
            return b;
        }

        return key switch
        {
            "OkBrush" => Ok,
            "InfoBrush" => Info,
            "AccentBrush" => Accent,
            "DangerBrush" => Danger,
            _ => Muted,
        };
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
