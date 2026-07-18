using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using VideoSplitJoiner.Core.Thumbnails;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// WPF-free view model for the scrub-bar hover thumbnail (T-078). The view feeds it the hovered time
/// + cursor X (from <see cref="ScrubSlider"/> <c>MouseMove</c>) and toggles visibility on enter/leave;
/// this VM debounces + coalesces the hover, fetches a frame path from the Core
/// <see cref="IThumbnailService"/> (T-077), and exposes the result as primitives the view binds to a
/// <c>Popup</c> (a temp jpg PATH the view loads into a frozen <c>BitmapImage</c>, the hovered time as a
/// <c>mm:ss</c> label, a visibility flag, and the horizontal cursor offset for popup placement).
///
/// <para><b>Debounce + coalesce (the crux, D2):</b> each hover update cancels the prior in-flight
/// request (<see cref="CancellationTokenSource"/> swap — latest-wins), then waits a short debounce
/// window before touching ffmpeg so a fast sweep does not flood the service. Only when a grab resolves
/// for the STILL-current request does <see cref="HoverThumbnailPath"/> update; a superseded grab is
/// dropped. The await happens off the UI thread; the result is marshalled back onto the captured
/// synchronization context (the WPF dispatcher in the app, the test thread under xUnit) exactly like the
/// codebase's <see cref="OperationViewModel"/> progress channel.</para>
///
/// <para><b>Cleanup:</b> <see cref="SetInput"/> (new load) and <see cref="Clear"/> (unload) hide the
/// preview and sweep the service's temp cache for the previous file via
/// <see cref="IThumbnailService.Clear"/>; <see cref="MouseLeave"/> hides without touching the cache.
/// Best-effort throughout — a failed grab simply shows nothing (never throws, never a stuck popup).</para>
/// </summary>
public sealed class ThumbnailPreviewViewModel : ObservableObject
{
    /// <summary>Thumbnail width handed to the service (matches the popup image width).</summary>
    public const int ThumbnailWidth = 160;

    /// <summary>Default debounce settle window before a hover triggers an ffmpeg grab.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(60);

    private readonly IThumbnailService _thumbnails;
    private readonly TimeSpan _debounce;

    // Injectable delay seam: production is Task.Delay; tests pass an immediate/controllable delay so
    // debounce is deterministic without real wall-clock waits.
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // Marshals the resolved path back onto the UI thread. Progress<T> captures the current
    // SynchronizationContext (UI dispatcher in the app, test thread under xUnit) — same pattern the
    // OperationViewModel uses for progress.
    private readonly IProgress<PathResult> _postResult;

    private string? _inputPath;
    private TimeSpan? _duration;

    private TimeSpan _hoverTime;
    private double _hoverOffsetX;
    private string? _hoverThumbnailPath;
    private bool _isHovering;

    // The current (latest) hover request. Swapped — and the prior one cancelled — on every hover
    // update, so only the newest request survives to set the path (latest-wins coalesce).
    private CancellationTokenSource? _requestCts;

    // A monotonically increasing id stamped on each request; a resolved grab only commits its path when
    // its id is still the newest, so a superseded (but not-yet-cancelled) grab can never clobber a newer
    // result even if both complete.
    private long _requestId;

    /// <summary>Production ctor: real <see cref="Task.Delay"/> debounce over the Core service.</summary>
    public ThumbnailPreviewViewModel(IThumbnailService thumbnails)
        : this(thumbnails, DefaultDebounce, (d, ct) => Task.Delay(d, ct))
    {
    }

    /// <summary>
    /// Testable ctor: <paramref name="debounce"/> is the settle window and <paramref name="delay"/> is
    /// the wait seam (tests pass an immediate or gated delay so debounce/coalesce are deterministic).
    /// </summary>
    public ThumbnailPreviewViewModel(
        IThumbnailService thumbnails,
        TimeSpan debounce,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _debounce = debounce > TimeSpan.Zero ? debounce : DefaultDebounce;

        _postResult = new Progress<PathResult>(ApplyResult);
    }

    // ---- Bound state ------------------------------------------------------------------------

    /// <summary>The hovered time under the cursor — shown as the popup's <c>mm:ss</c> label.</summary>
    public TimeSpan HoverTime
    {
        get => _hoverTime;
        private set
        {
            if (SetProperty(ref _hoverTime, value))
            {
                OnPropertyChanged(nameof(HoverTimeText));
            }
        }
    }

    /// <summary>The hovered time formatted <c>mm:ss</c> (or <c>h:mm:ss</c> past an hour) for the label.</summary>
    public string HoverTimeText => FormatClock(_hoverTime);

    /// <summary>
    /// The horizontal cursor offset (px from the slider's left edge) the popup is placed at, so the
    /// preview follows the cursor along the bar. The view clamps it to keep the popup on-screen.
    /// </summary>
    public double HoverOffsetX
    {
        get => _hoverOffsetX;
        private set => SetProperty(ref _hoverOffsetX, value);
    }

    /// <summary>
    /// The temp jpg PATH of the current hover frame, or null when none is available yet / the grab
    /// failed. The view loads this into a frozen <c>BitmapImage</c>; null/empty shows no image.
    /// </summary>
    public string? HoverThumbnailPath
    {
        get => _hoverThumbnailPath;
        private set
        {
            if (SetProperty(ref _hoverThumbnailPath, value))
            {
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    /// <summary>True when a non-empty thumbnail path is available (the popup image has something to show).</summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(_hoverThumbnailPath);

    /// <summary>
    /// True while the popup should be shown — the cursor is over the bar AND a file is loaded with a
    /// known duration. Bound to the popup's <c>IsOpen</c>. Goes false on <see cref="MouseLeave"/>,
    /// <see cref="SetInput"/>, and <see cref="Clear"/>.
    /// </summary>
    public bool IsThumbnailVisible
    {
        get => _isHovering && _inputPath is not null && _duration is { } d && d > TimeSpan.Zero;
        private set
        {
            // Backing store is _isHovering; recompute + notify via that seam so the computed rule holds.
            if (_isHovering != value)
            {
                _isHovering = value;
                OnPropertyChanged(nameof(IsThumbnailVisible));
            }
        }
    }

    // ---- Wiring from the owning VM ----------------------------------------------------------

    /// <summary>
    /// Point the preview at a newly-loaded file (called from the Split VM's load). Sweeps the previous
    /// file's temp cache, resets all hover state, and hides the popup. A null/empty path clears the
    /// input (same as <see cref="Clear"/> minus the explicit hide message, which it also does).
    /// </summary>
    public void SetInput(string? inputPath, TimeSpan? duration)
    {
        // Sweep the OUTGOING file's temp thumbnails so a new load never leaks the prior file's cache.
        SweepPrevious();

        _inputPath = string.IsNullOrWhiteSpace(inputPath) ? null : inputPath;
        _duration = duration;

        CancelInFlight();
        HoverThumbnailPath = null;
        IsThumbnailVisible = false; // sets _isHovering = false
        OnPropertyChanged(nameof(IsThumbnailVisible));
    }

    /// <summary>Keep the known duration in sync (the player learns it after the load). Re-raises visibility.</summary>
    public void SetDuration(TimeSpan? duration)
    {
        _duration = duration;
        OnPropertyChanged(nameof(IsThumbnailVisible));
    }

    /// <summary>
    /// Clear the preview entirely (called from the player/Split VM's unload). Sweeps the current file's
    /// temp cache, drops the input, cancels any in-flight grab, and hides the popup.
    /// </summary>
    public void Clear()
    {
        SweepPrevious();

        _inputPath = null;
        _duration = null;

        CancelInFlight();
        HoverThumbnailPath = null;
        IsThumbnailVisible = false;
        OnPropertyChanged(nameof(IsThumbnailVisible));
    }

    // ---- Hover input (from the view) --------------------------------------------------------

    /// <summary>The cursor entered the scrub bar — show the popup (if a file is loaded).</summary>
    public void MouseEnter()
    {
        IsThumbnailVisible = true;
    }

    /// <summary>
    /// The cursor left the scrub bar — hide the popup, drop the current frame, and cancel any in-flight
    /// grab so a late result can't re-show a stale thumbnail. Does NOT sweep the cache (the file is
    /// still loaded; cached frames are reused on the next hover).
    /// </summary>
    public void MouseLeave()
    {
        IsThumbnailVisible = false;
        CancelInFlight();
        HoverThumbnailPath = null;
    }

    /// <summary>
    /// A hover sample from the view: the cursor is at <paramref name="offsetX"/> px along the bar,
    /// mapping to <paramref name="time"/>. Updates the label + popup position immediately, then
    /// debounces + coalesces an ffmpeg grab for the frame at that time (latest-wins). No-op (beyond
    /// showing the label) when no file is loaded.
    /// </summary>
    public void UpdateHover(TimeSpan time, double offsetX)
    {
        // The label + popup position track the cursor instantly, even before a frame arrives.
        HoverTime = time;
        HoverOffsetX = offsetX;

        if (_inputPath is null)
        {
            return;
        }

        IsThumbnailVisible = true;

        // Latest-wins: cancel the prior request and start a fresh one for THIS hover.
        CancelInFlight();
        var cts = new CancellationTokenSource();
        _requestCts = cts;
        var id = ++_requestId;

        _ = GrabAsync(_inputPath, time, id, cts);
    }

    // ---- Async grab -------------------------------------------------------------------------

    /// <summary>
    /// Debounce → fetch → marshal-back for one hover request. Waits the debounce window (cancellable by
    /// a newer hover), calls the service off the UI thread, and — only if this request is still the
    /// current one — posts the resolved path back onto the captured context to set
    /// <see cref="HoverThumbnailPath"/>. Best-effort: any failure resolves to null (shows nothing).
    /// </summary>
    private async Task GrabAsync(string inputPath, TimeSpan time, long id, CancellationTokenSource cts)
    {
        try
        {
            // Debounce: settle before touching ffmpeg. A newer hover cancels this wait.
            await _delay(_debounce, cts.Token).ConfigureAwait(false);

            var path = await _thumbnails
                .GetThumbnailAsync(inputPath, time, ThumbnailWidth, cts.Token)
                .ConfigureAwait(false);

            if (cts.Token.IsCancellationRequested)
            {
                return;
            }

            // Marshal the result back onto the UI thread; ApplyResult re-checks currency before committing.
            _postResult.Report(new PathResult(id, path));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer hover / a clear — drop silently (never clobbers a newer result).
        }
        catch
        {
            // Best-effort — any other failure shows nothing.
        }
        finally
        {
            // Retire this request's CTS if it is still the tracked one (a newer hover may have replaced it).
            if (ReferenceEquals(_requestCts, cts))
            {
                _requestCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Commit a resolved grab on the UI thread — but only when it is still the newest request (id match)
    /// and the cursor is still over the bar. A stale or post-leave result is dropped. A null path leaves
    /// the popup showing nothing rather than a stale image.
    /// </summary>
    private void ApplyResult(PathResult result)
    {
        // A newer hover superseded this grab, or the cursor already left → drop it.
        if (result.Id != _requestId || !_isHovering)
        {
            return;
        }

        HoverThumbnailPath = string.IsNullOrEmpty(result.Path) ? null : result.Path;
    }

    // ---- Helpers ----------------------------------------------------------------------------

    /// <summary>Cancel + dispose the in-flight request's CTS (if any) so a superseded grab is dropped.</summary>
    private void CancelInFlight()
    {
        var cts = _requestCts;
        _requestCts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already retired by its own finally — nothing to cancel.
        }
    }

    /// <summary>Best-effort sweep of the current input's temp thumbnails (on load/clear). Never throws.</summary>
    private void SweepPrevious()
    {
        if (_inputPath is { } prev)
        {
            _thumbnails.Clear(prev);
        }
    }

    /// <summary>Format a time as <c>mm:ss</c> (or <c>h:mm:ss</c> past an hour) for the hover label.</summary>
    private static string FormatClock(TimeSpan t)
    {
        var a = t < TimeSpan.Zero ? t.Negate() : t;
        return a.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)a.TotalHours}:{a.Minutes:00}:{a.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{a.Minutes:00}:{a.Seconds:00}");
    }

    /// <summary>A resolved grab: its request id + the path (or null) the service returned.</summary>
    private readonly record struct PathResult(long Id, string? Path);
}
