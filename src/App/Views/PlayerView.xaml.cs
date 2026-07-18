using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
