using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Unosquare.FFME;
using Unosquare.FFME.Common;
using VideoSplitJoiner.Core.Errors;

namespace VideoSplitJoiner.App.Media;

/// <summary>
/// FFME-backed <see cref="IMediaPlayer"/> that wraps an <see cref="Unosquare.FFME.MediaElement"/>
/// living in the view. FFME decodes through ffmpeg, so it plays formats WPF's native
/// <c>MediaElement</c> could not (HEVC, many container/codec combos). The element is handed in
/// (ctor or <see cref="Attach"/>) with <c>LoadedBehavior=Manual</c> and <c>ScrubbingEnabled=true</c>
/// so this class fully drives transport and Open shows the first frame without auto-playing.
/// </summary>
/// <remarks>
/// FFME's transport methods are asynchronous (they return awaitables). The <see cref="IMediaPlayer"/>
/// surface is synchronous, so each call is adapted fire-and-forget: the awaitable is observed on a
/// continuation that routes any exception to <see cref="Failed"/>. FFME raises
/// <see cref="Unosquare.FFME.MediaElement.PositionChanged"/> natively, so — unlike the retired
/// WPF <c>MediaElementPlayer</c> — there is no <c>DispatcherTimer</c>. This impl is thin WPF plumbing
/// and cannot run headlessly, so it is NOT unit-tested; it only has to compile. Playback is verified
/// live on a real desktop via <c>app-run</c>.
/// </remarks>
public sealed class FfmeMediaPlayer : IMediaPlayer, IReopenTarget
{
    /// <summary>
    /// Cap the on-screen preview to ~1080p tall (T-024). A 4K source is decoded/rendered at this
    /// height so the WPF UI thread is not saturated pushing 3840×2160 BGRA frames every tick; the
    /// split still runs at full source resolution (it is <c>-c copy</c>, never decoded).
    /// </summary>
    private const int MaxPreviewHeight = 1080;

    private MediaElement? _element;
    private TimeSpan? _duration;
    private bool _isPlaying;

    // ---- Close→Open lifecycle guard (T-080) -------------------------------------------------
    // FFME's Open/Close are async commands. Calling Open() while a prior Close() (or Open()) is
    // still in flight — the element is IsClosing / IsOpening / IsChanging — is a known NATIVE crash
    // spot (AccessViolation that bypasses managed handlers). The reproduction is: split → Clear
    // (fire-and-forget Close) → immediately drag a new video (Open) before the close settled.
    //
    // The guard (MediaReopenGuard, WPF-free + unit-tested) sequences it: every Open registers a
    // lifecycle generation and awaits this element out of any transitional state (via IReopenTarget,
    // implemented below) before issuing _element.Open(...). A newer Open/Unload supersedes an older
    // pending open. This class feeds the element's state; the guard owns the decision.
    private readonly MediaReopenGuard _reopenGuard;

    /// <summary>Create an unattached player; call <see cref="Attach"/> once the element exists.</summary>
    public FfmeMediaPlayer()
    {
        _reopenGuard = new MediaReopenGuard(this);
    }

    /// <summary>Create a player already bound to <paramref name="element"/>.</summary>
    public FfmeMediaPlayer(MediaElement element)
        : this()
    {
        Attach(element);
    }

    /// <summary>
    /// Bind this player to the view's FFME <see cref="MediaElement"/>. Forces manual/scrubbing mode
    /// (so Open shows the first frame without playing) and hooks the media events. Safe to call once;
    /// re-attaching swaps the element.
    /// </summary>
    public void Attach(MediaElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        Detach();

        _element = element;
        // Manual playback state = FFME does not auto-play on Open; scrubbing shows the first frame.
        _element.LoadedBehavior = MediaPlaybackState.Manual;
        _element.UnloadedBehavior = MediaPlaybackState.Manual;
        _element.ScrubbingEnabled = true;

        // Pre-open hook (T-024): configure hardware decoding + a downscaled preview filter before
        // FFME opens the stream. MediaOpening carries e.Options (a MediaOptions) and e.Info (the
        // probed MediaInfo with per-stream pixel dimensions).
        _element.MediaOpening += OnMediaOpening;
        _element.MediaOpened += OnMediaOpened;
        _element.MediaEnded += OnMediaEnded;
        _element.MediaFailed += OnMediaFailed;
        _element.PositionChanged += OnPositionChanged;
    }

    private void Detach()
    {
        if (_element is not null)
        {
            // T-080: supersede any pending open bound to the outgoing element so a stale settle-then-
            // open can never fire against a swapped/detached element.
            _reopenGuard.NotifySuperseded();

            _element.MediaOpening -= OnMediaOpening;
            _element.MediaOpened -= OnMediaOpened;
            _element.MediaEnded -= OnMediaEnded;
            _element.MediaFailed -= OnMediaFailed;
            _element.PositionChanged -= OnPositionChanged;
        }
    }

    public TimeSpan Position
    {
        get => _element?.ActualPosition ?? _element?.Position ?? TimeSpan.Zero;
        set => Seek(value);
    }

    public TimeSpan? Duration => _duration;

    public bool IsPlaying => _isPlaying;

    /// <summary>
    /// Output volume 0..1, mapped to FFME's <see cref="MediaElement.Volume"/> (a double
    /// DependencyProperty). Unlike the transport calls, this is a plain synchronous property set on
    /// the control — no <see cref="Run"/> adaption needed. Null-guarded like every other member; the
    /// getter returns 1.0 (full) before an element is attached.
    /// </summary>
    public double Volume
    {
        get => _element?.Volume ?? 1.0;
        set
        {
            if (_element is not null)
            {
                _element.Volume = value;
            }
        }
    }

    /// <summary>Mute flag, mapped to FFME's <see cref="MediaElement.IsMuted"/> (a bool DependencyProperty).</summary>
    public bool IsMuted
    {
        get => _element?.IsMuted ?? false;
        set
        {
            if (_element is not null)
            {
                _element.IsMuted = value;
            }
        }
    }

    /// <summary>Playback speed, mapped to FFME's <see cref="MediaElement.SpeedRatio"/> (a double DependencyProperty).</summary>
    public double SpeedRatio
    {
        get => _element?.SpeedRatio ?? 1.0;
        set
        {
            if (_element is not null)
            {
                _element.SpeedRatio = value;
            }
        }
    }

    public event EventHandler? PositionChanged;

    public event EventHandler? Seeked;

    public event EventHandler? DurationAvailable;

    public event EventHandler? Ended;

    public event EventHandler<string>? Failed;

    public void Open(string path)
    {
        if (_element is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _isPlaying = false;
        _duration = null;

        // T-080: register this open with the lifecycle guard (superseding any earlier pending open),
        // then sequence Close→Open safely — the actual _element.Open(...) runs only once the element
        // has left any transitional (IsClosing/IsOpening/IsChanging) state. This is the crash fix: a
        // fresh Open right after Clear's fire-and-forget Close must not hit the element mid-close.
        var gen = _reopenGuard.RequestOpen();
        _ = OpenWhenSettledAsync(path, gen);
    }

    /// <summary>
    /// Open <paramref name="path"/> once the lifecycle guard reports the element is settled (T-080).
    /// The guard awaits the element out of any transitional state — a prior <see cref="Unload"/>'s
    /// async Close, or an in-flight Open — on the UI dispatcher so the thread is never blocked. A
    /// newer <see cref="Open"/> / <see cref="Unload"/> supersedes this request (it drops without
    /// opening); a settle timeout surfaces a friendly failure rather than risk the native
    /// Open-while-closing crash. Any managed fault is routed to <see cref="Failed"/>, mirroring
    /// <see cref="Run"/>.
    /// </summary>
    private async Task OpenWhenSettledAsync(string path, long gen)
    {
        try
        {
            var decision = await _reopenGuard.WaitUntilReopenableAsync(gen).ConfigureAwait(true);

            switch (decision)
            {
                case ReopenDecision.Superseded:
                    // A newer Open/Unload now owns the element → drop this stale open silently.
                    return;

                case ReopenDecision.Timeout:
                    // The prior close never settled → opening is unsafe. Surface a recoverable failure.
                    RaiseFailed("The previous video is still closing — please try loading again.");
                    return;

                case ReopenDecision.Open:
                    if (_element is null)
                    {
                        return;
                    }

                    // T-131: a UNC path whose SERVER NAME is not a legal URI hostname (a space, as on
                    // many consumer NAS boxes) makes new Uri throw, and the catch below used to surface
                    // .NET's "Invalid URI: The hostname could not be parsed." verbatim. Decide first, and
                    // explain the refusal in terms the user can act on.
                    if (!MediaSourceUri.TryCreate(path, out var source) || source is null)
                    {
                        // Record the offending path. The original failure wrote nothing at all, which is
                        // why diagnosing it needed a from-scratch reproduction of the path shape.
                        TryLogRefusal(path);
                        RaiseFailed(MediaSourceUri.ExplainRefusal(path));
                        return;
                    }

                    // LoadedBehavior=Manual means Open loads + shows the first frame without playing;
                    // the MediaOpened event then supplies the duration.
                    Run(() => _element!.Open(source));
                    return;
            }
        }
        catch (Exception ex)
        {
            RaiseFailed(ex.Message);
        }
    }

    /// <summary>
    /// Best-effort note that a path could not be expressed as a media address (T-131), so the next
    /// report of this is diagnosable from the log instead of by reproducing the path shape by hand.
    /// Never throws and never blocks the refusal it is recording.
    /// </summary>
    private static void TryLogRefusal(string? path)
    {
        try
        {
            new ErrorLogWriter().TryWrite(
                operation: "preview-open-refused",
                command: path ?? string.Empty,
                exitCode: 0,
                fullStdErr: MediaSourceUri.ExplainRefusal(path));
        }
        catch
        {
            // Logging must never turn a handled refusal into a crash.
        }
    }

    // ---- IReopenTarget (T-080): the element-state seam the guard reads --------------------------

    /// <summary>
    /// True when the FFME element is safe to (re)open: it is not mid-close, mid-open, or changing
    /// components. The guard polls this between its settle waits; a null element reads as reopenable
    /// (nothing to close), and a detached element is handled by <see cref="IsDetached"/>.
    /// </summary>
    bool IReopenTarget.IsReopenable
    {
        get
        {
            var element = _element;
            if (element is null)
            {
                return true;
            }

            return !element.IsClosing && !element.IsOpening && !element.IsChanging;
        }
    }

    /// <summary>True when no element is attached — the guard stops waiting on a detached player.</summary>
    bool IReopenTarget.IsDetached => _element is null;

    public void Play()
    {
        if (_element is null)
        {
            return;
        }

        _isPlaying = true;
        Run(() => _element.Play());
    }

    public void Pause()
    {
        if (_element is null)
        {
            return;
        }

        _isPlaying = false;
        Run(() => _element.Pause());
    }

    public void Stop()
    {
        if (_element is null)
        {
            return;
        }

        _isPlaying = false;
        // FFME Stop resets the position to the start; surface the position change to listeners.
        Run(() => _element.Stop(), () => PositionChanged?.Invoke(this, EventArgs.Empty));
    }

    public void Seek(TimeSpan t)
    {
        if (_element is null)
        {
            return;
        }

        var clamped = Clamp(t);
        // On completion of the async seek, surface the new position AND signal seek-completion so the
        // VM can release its seek-target hold deterministically (T-033).
        Run(
            () => _element.Seek(clamped),
            () =>
            {
                PositionChanged?.Invoke(this, EventArgs.Empty);
                Seeked?.Invoke(this, EventArgs.Empty);
            });
    }

    public void Unload()
    {
        if (_element is null)
        {
            return;
        }

        // Reset our own transport state first so IsPlaying/Duration read blank immediately.
        _isPlaying = false;
        _duration = null;

        // T-080: supersede any pending open so a stale Open queued before this Unload never fires
        // against the now-closing element (the crash window works the other way too: Open then a fast
        // Clear). The next real Open will register a fresh generation and wait for THIS close to settle.
        _reopenGuard.NotifySuperseded();

        // Close the media (async, like the other transport calls) so the decode stops and the
        // preview surface goes blank. FFME's Source DP is read-only (driven by Open/Close), so Close
        // is the surface-blanking path; on completion surface a PositionChanged so listeners refresh.
        Run(() => _element.Close(), () => PositionChanged?.Invoke(this, EventArgs.Empty));
    }

    public void StepFrame(int direction)
    {
        if (_element is null || direction == 0)
        {
            return;
        }

        // Frame-step is a paused operation: if we were playing, pause first so the single-frame
        // seek lands on a stable frame rather than fighting the play loop.
        if (_isPlaying)
        {
            _isPlaying = false;
            Run(() => _element.Pause());
        }

        // StepForward()/StepBackward() are FFME's single-frame seeks; both return the same
        // ConfiguredTaskAwaitable<bool> as Play()/Seek()/Pause(), so they adapt through the same
        // fire-and-forget Run() (faults routed to Failed). Surface the position change to listeners.
        Run(
            () => direction > 0 ? _element.StepForward() : _element.StepBackward(),
            () => PositionChanged?.Invoke(this, EventArgs.Empty));
    }

    private TimeSpan Clamp(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return _duration is { } d && t > d ? d : t;
    }

    /// <summary>
    /// Adapt an async FFME transport call to the synchronous <see cref="IMediaPlayer"/> surface:
    /// start the operation, and on a UI-thread continuation route any fault to <see cref="Failed"/>
    /// or run <paramref name="onSuccess"/>. Fire-and-forget — the caller does not await.
    /// </summary>
    private void Run(Func<ConfiguredTaskAwaitable<bool>> op, Action? onSuccess = null)
    {
        try
        {
            var awaiter = op().GetAwaiter();
            awaiter.OnCompleted(() =>
            {
                try
                {
                    awaiter.GetResult();
                    onSuccess?.Invoke();
                }
                catch (Exception ex)
                {
                    RaiseFailed(ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            RaiseFailed(ex.Message);
        }
    }

    private void RaiseFailed(string reason)
    {
        _isPlaying = false;
        Failed?.Invoke(this, string.IsNullOrWhiteSpace(reason) ? "The video could not be played." : reason);
    }

    private void OnPositionChanged(object? sender, PositionChangedEventArgs e) =>
        PositionChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Pre-open configuration hook (T-024). Runs before FFME decodes anything:
    /// <list type="number">
    /// <item>enables hardware-accelerated decoding (D3D11VA / DXVA2 / any device ffmpeg reports
    /// compatible with the video stream) by handing the probed device list to
    /// <see cref="MediaOptions.VideoHardwareDevices"/>; and</item>
    /// <item>installs a <c>scale=W:H</c> preview filter (<see cref="MediaOptions.VideoFilter"/>)
    /// capping the preview at <see cref="MaxPreviewHeight"/> so a 4K source renders at ~1080p.</item>
    /// </list>
    /// Both steps are best-effort and independently wrapped: a HW-init failure or a filter-build
    /// failure falls back silently to software / native-resolution decode — never a crash. The
    /// source and the eventual cut are untouched (split is <c>-c copy</c>).
    /// </summary>
    private void OnMediaOpening(object? sender, MediaOpeningEventArgs e)
    {
        var video = FindVideoStream(e);

        // --- (1) Hardware decoding -----------------------------------------------------------
        try
        {
            // The video stream carries the list of hardware devices ffmpeg found compatible with
            // its codec (D3D11VA / DXVA2 / CUDA / ...). Handing this array to VideoHardwareDevices
            // lets FFME pick the first that initializes; an empty array = software decode.
            var devices = video?.HardwareDevices;
            if (devices is { Count: > 0 })
            {
                e.Options.VideoHardwareDevices = devices.ToArray();
            }
        }
        catch (Exception ex)
        {
            // HW accel is a best-effort optimization — never fail the open over it.
            Debug.WriteLine($"[FFME] Hardware-decode setup skipped: {ex.Message}");
        }

        // --- (2) Downscaled preview filter ---------------------------------------------------
        try
        {
            if (video is not null && string.IsNullOrWhiteSpace(e.Options.VideoFilter))
            {
                var filter = PreviewScale.BuildScaleFilter(video.PixelWidth, video.PixelHeight, MaxPreviewHeight);
                if (filter is not null)
                {
                    // scale runs on CPU frames; FFME downloads hardware frames before this filter,
                    // so HW-decode + software-scale compose cleanly. Preview-only — cut is full-res.
                    e.Options.VideoFilter = filter;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FFME] Preview downscale filter skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Find the first video stream in the probed <see cref="MediaInfo"/>, matching on the
    /// string <c>CodecTypeName</c> ("video") so this class does not take a compile-time dependency
    /// on the FFmpeg.AutoGen <c>AVMediaType</c> enum. Returns <c>null</c> if there is none.
    /// </summary>
    private static StreamInfo? FindVideoStream(MediaOpeningEventArgs e) =>
        e.Info?.Streams?.Values.FirstOrDefault(
            s => string.Equals(s.CodecTypeName, "video", StringComparison.OrdinalIgnoreCase));

    private void OnMediaOpened(object? sender, MediaOpenedEventArgs e)
    {
        _duration = _element?.NaturalDuration;
        DurationAvailable?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        _isPlaying = false;
        Ended?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaFailed(object? sender, MediaFailedEventArgs e) =>
        RaiseFailed(e.ErrorException?.Message ?? "The video could not be played.");
}
