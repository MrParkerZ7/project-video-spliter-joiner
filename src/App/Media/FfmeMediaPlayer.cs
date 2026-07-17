using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Unosquare.FFME;
using Unosquare.FFME.Common;

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
public sealed class FfmeMediaPlayer : IMediaPlayer
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

    /// <summary>Create an unattached player; call <see cref="Attach"/> once the element exists.</summary>
    public FfmeMediaPlayer()
    {
    }

    /// <summary>Create a player already bound to <paramref name="element"/>.</summary>
    public FfmeMediaPlayer(MediaElement element)
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
        // LoadedBehavior=Manual means Open loads + shows the first frame without playing; the
        // MediaOpened event then supplies the duration. No explicit Play/Pause nudge is needed.
        Run(() => _element.Open(new Uri(path, UriKind.RelativeOrAbsolute)));
    }

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
