using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.ViewModels;

namespace VideoSplitJoiner.App.Views;

/// <summary>
/// Code-behind for the video preview player. Keeps only the WPF seam: on <c>Loaded</c> it hands the
/// view's FFME <see cref="Unosquare.FFME.MediaElement"/> to the <see cref="FfmeMediaPlayer"/> that
/// backs the bound <see cref="PlayerViewModel"/>. All transport/timeline logic lives WPF-free in the
/// VM. If the VM's player is not a <see cref="FfmeMediaPlayer"/> (e.g. a design-time or test
/// stand-in) the attach is skipped silently.
/// </summary>
public partial class PlayerView : UserControl
{
    private bool _attached;

    public PlayerView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached || DataContext is not PlayerViewModel vm)
        {
            return;
        }

        if (vm.PlayerControl is FfmeMediaPlayer ffmePlayer)
        {
            ffmePlayer.Attach(Media);
            _attached = true;
        }
    }

    /// <summary>Copy the preview-unavailable reason to the clipboard (T-037). Best-effort.</summary>
    private void OnCopyPreviewErrorClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is PlayerViewModel vm)
        {
            ErrorActions.TryCopy(vm.PreviewFailedReason);
        }
    }

    // ---- Hover thumbnail (T-078) ------------------------------------------------------------
    // The popup image width (matches ThumbnailPreviewViewModel.ThumbnailWidth + the XAML box). Used to
    // center the popup on the cursor and clamp it within the bar so it never clips off either end.
    private const double HoverPopupWidth = 160d;

    /// <summary>
    /// The cursor entered the scrub bar — show the hover popup (no-op in the VM if no file is loaded).
    /// Display-only: does not touch the seek/scrub path.
    /// </summary>
    private void OnScrubMouseEnter(object sender, MouseEventArgs e)
    {
        if (DataContext is PlayerViewModel vm)
        {
            vm.Thumbnail.MouseEnter();
        }
    }

    /// <summary>The cursor left the scrub bar — hide the hover popup and drop the current frame.</summary>
    private void OnScrubMouseLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is PlayerViewModel vm)
        {
            vm.Thumbnail.MouseLeave();
        }
    }

    /// <summary>
    /// The cursor moved over the scrub bar (T-078) — map its X to a time and feed the hover preview VM.
    /// PASSIVE: it does NOT set <c>e.Handled</c> and never issues a seek, so click-to-seek (G-028) and
    /// thumb-drag scrub (T-033/T-051) are completely unaffected — this only drives the display-only
    /// hover popup. Fraction = cursorX / trackWidth (guarded against a zero width); the horizontal
    /// offset centers the 160-wide popup on the cursor, clamped so it never clips off either bar end.
    /// </summary>
    private void OnScrubMouseMove(object sender, MouseEventArgs e)
    {
        if (DataContext is not PlayerViewModel vm)
        {
            return;
        }

        var width = ScrubSlider.ActualWidth;
        if (width <= 0d)
        {
            return;
        }

        var x = e.GetPosition(ScrubSlider).X;
        var fraction = Math.Clamp(x / width, 0d, 1d);
        var time = TimeSpan.FromSeconds(fraction * vm.DurationSeconds);

        // Center the popup on the cursor, then clamp within [0, width - popupWidth] so it stays on-bar.
        var offset = x - (HoverPopupWidth / 2d);
        var maxOffset = Math.Max(0d, width - HoverPopupWidth);
        offset = Math.Clamp(offset, 0d, maxOffset);

        vm.Thumbnail.UpdateHover(time, offset);
    }

    /// <summary>
    /// The user grabbed the scrub thumb — enter live-scrub mode (T-033/T-051). Playback echoes are
    /// suppressed while dragging; the frame instead follows the pin via <c>OnScrubDragDelta</c>.
    /// </summary>
    private void OnScrubDragStarted(object sender, DragStartedEventArgs e)
    {
        if (DataContext is PlayerViewModel vm)
        {
            vm.BeginUserScrub();
        }
    }

    /// <summary>
    /// The scrub thumb moved (fires continuously during the drag) — feed the current slider value to
    /// the VM's live-scrub path so the video frame follows the pin. Seeks are coalesced + throttled in
    /// the VM so a fast drag never backs up a seek queue (T-051).
    /// </summary>
    private void OnScrubDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (DataContext is PlayerViewModel vm)
        {
            vm.ScrubPreview(TimeSpan.FromSeconds(ScrubSlider.Value));
        }
    }

    /// <summary>
    /// The user released the scrub thumb — issue the final exact seek to the slider's final value and
    /// arm the seek-target hold so a stale echo can't pop the playhead back (T-033).
    /// </summary>
    private void OnScrubDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is PlayerViewModel vm)
        {
            vm.EndUserScrub(ScrubSlider.Value);
        }
    }
}
