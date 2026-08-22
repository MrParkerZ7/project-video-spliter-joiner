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
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts|All files|*.*",
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
            vm.AddFilesCommand.Execute(dialog.FileNames);
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
        HandleDroppedFiles(paths, vm);
    }

    /// <summary>
    /// Drop routing extracted for testability: filters the paths to videos (order preserved) and, if
    /// any, adds ALL of them via <see cref="BulkCutViewModel.AddFilesCommand"/>. Returns the filtered
    /// video paths; empty-after-filter is a no-op.
    /// </summary>
    internal static IReadOnlyList<string> HandleDroppedFiles(string[] paths, BulkCutViewModel vm)
    {
        var videos = VideoFileFilter.AcceptVideoFiles(paths);
        if (videos.Count > 0)
        {
            vm.AddFilesCommand.Execute(videos.ToArray());
        }

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
        if (TryParseClock(box.Text, out var t))
        {
            if (isIntro)
            {
                row.IntroEnd.Requested = t;
            }
            else if (row.OutroStart is not null)
            {
                row.OutroStart.Requested = t;
            }
        }

        // Normalize the field back to the snapped truth (also reverts an unparseable entry).
        BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateTarget();
    }

    /// <summary>Parse <c>mm:ss.f</c> / <c>h:mm:ss.f</c> / plain seconds into a non-negative TimeSpan.</summary>
    internal static bool TryParseClock(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length > 3)
        {
            return false;
        }

        double h = 0, m = 0, s;
        try
        {
            if (parts.Length == 3)
            {
                h = double.Parse(parts[0], CultureInfo.InvariantCulture);
                m = double.Parse(parts[1], CultureInfo.InvariantCulture);
                s = double.Parse(parts[2], CultureInfo.InvariantCulture);
            }
            else if (parts.Length == 2)
            {
                m = double.Parse(parts[0], CultureInfo.InvariantCulture);
                s = double.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            else
            {
                s = double.Parse(parts[0], CultureInfo.InvariantCulture);
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }

        var total = (h * 3600d) + (m * 60d) + s;
        if (total < 0 || double.IsNaN(total) || double.IsInfinity(total))
        {
            return false;
        }

        value = TimeSpan.FromSeconds(total);
        return true;
    }

    // ---- Cut-profile save: themed inline name popup (T-103) ---------------------------------

    /// <summary>
    /// Open the themed inline name input under the Save button (not a raw WPF dialog). Pre-fills with the
    /// currently-selected profile's name (handy for a "save over" upsert) and focuses/selects the field.
    /// </summary>
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
            SaveProfilePopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnConfirmSaveProfileClicked(object sender, RoutedEventArgs e) => CommitSaveProfile();

    private void OnCancelSaveProfileClicked(object sender, RoutedEventArgs e) => SaveProfilePopup.IsOpen = false;

    // ---- Profile thumbnail: upload / clear (T-107) ------------------------------------------

    /// <summary>
    /// Pick an image and hand it to the VM to store as the SELECTED profile's thumbnail (T-107). The VM
    /// copies it into the T-106 <c>ProfileThumbnailStore</c>, folds the stored path onto the profile, and
    /// re-persists — best-effort, so a cancel / bad pick is a no-op. The OpenFileDialog lives here (View),
    /// keeping the VM WPF-free.
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
        SaveProfilePopup.IsOpen = false;
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
