using System;
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

        _element.MediaOpened += OnMediaOpened;
        _element.MediaEnded += OnMediaEnded;
        _element.MediaFailed += OnMediaFailed;
        _element.PositionChanged += OnPositionChanged;
    }

    private void Detach()
    {
        if (_element is not null)
        {
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

    public event EventHandler? PositionChanged;

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
        Run(() => _element.Seek(clamped), () => PositionChanged?.Invoke(this, EventArgs.Empty));
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
