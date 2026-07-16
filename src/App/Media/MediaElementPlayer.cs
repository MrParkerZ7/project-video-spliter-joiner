using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VideoSplitJoiner.App.Media;

/// <summary>
/// WPF-bound <see cref="IMediaPlayer"/> that wraps a <see cref="MediaElement"/> living in the view.
/// The element is handed in (ctor or <see cref="Attach"/>) with <c>LoadedBehavior=Manual</c> and
/// <c>ScrubbingEnabled=true</c> so this class fully drives transport. A ~200ms
/// <see cref="DispatcherTimer"/> pumps <see cref="PositionChanged"/> while playing; MediaElement's
/// own <c>MediaOpened</c>/<c>MediaEnded</c>/<c>MediaFailed</c> map to
/// <see cref="DurationAvailable"/>/<see cref="Ended"/>/<see cref="Failed"/>.
/// </summary>
/// <remarks>
/// This impl is thin plumbing over WPF types and cannot run headlessly, so it is NOT unit-tested;
/// it only has to compile. Playback logic is verified live on a real desktop via <c>app-run</c>.
/// </remarks>
public sealed class MediaElementPlayer : IMediaPlayer
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    private MediaElement? _element;
    private DispatcherTimer? _timer;
    private TimeSpan? _duration;
    private bool _isPlaying;

    /// <summary>Create an unattached player; call <see cref="Attach"/> once the element exists.</summary>
    public MediaElementPlayer()
    {
    }

    /// <summary>Create a player already bound to <paramref name="element"/>.</summary>
    public MediaElementPlayer(MediaElement element)
    {
        Attach(element);
    }

    /// <summary>
    /// Bind this player to the view's <see cref="MediaElement"/>. Forces manual/ scrubbing mode and
    /// hooks the element's media events. Safe to call once; re-attaching swaps the element.
    /// </summary>
    public void Attach(MediaElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        Detach();

        _element = element;
        _element.LoadedBehavior = MediaState.Manual;
        _element.UnloadedBehavior = MediaState.Manual;
        _element.ScrubbingEnabled = true;

        _element.MediaOpened += OnMediaOpened;
        _element.MediaEnded += OnMediaEnded;
        _element.MediaFailed += OnMediaFailed;

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += OnTick;
    }

    private void Detach()
    {
        if (_element is not null)
        {
            _element.MediaOpened -= OnMediaOpened;
            _element.MediaEnded -= OnMediaEnded;
            _element.MediaFailed -= OnMediaFailed;
        }

        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    public TimeSpan Position
    {
        get => _element?.Position ?? TimeSpan.Zero;
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

        StopTimer();
        _isPlaying = false;
        _duration = null;
        _element.Source = new Uri(path, UriKind.RelativeOrAbsolute);
        // A manual MediaElement needs a nudge to actually load and raise MediaOpened; stop after
        // opening leaves the first frame shown (ScrubbingEnabled) without playing.
        _element.Play();
        _element.Pause();
    }

    public void Play()
    {
        if (_element is null)
        {
            return;
        }

        _element.Play();
        _isPlaying = true;
        _timer?.Start();
    }

    public void Pause()
    {
        if (_element is null)
        {
            return;
        }

        _element.Pause();
        _isPlaying = false;
        StopTimer();
    }

    public void Stop()
    {
        if (_element is null)
        {
            return;
        }

        _element.Stop();
        _isPlaying = false;
        StopTimer();
        _element.Position = TimeSpan.Zero;
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(TimeSpan t)
    {
        if (_element is null)
        {
            return;
        }

        var clamped = Clamp(t);
        _element.Position = clamped;
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private TimeSpan Clamp(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return _duration is { } d && t > d ? d : t;
    }

    private void OnTick(object? sender, EventArgs e) => PositionChanged?.Invoke(this, EventArgs.Empty);

    private void OnMediaOpened(object? sender, RoutedEventArgs e)
    {
        if (_element is not null && _element.NaturalDuration.HasTimeSpan)
        {
            _duration = _element.NaturalDuration.TimeSpan;
        }

        DurationAvailable?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaEnded(object? sender, RoutedEventArgs e)
    {
        _isPlaying = false;
        StopTimer();
        Ended?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _isPlaying = false;
        StopTimer();
        Failed?.Invoke(this, e.ErrorException?.Message ?? "The video could not be played.");
    }

    private void StopTimer() => _timer?.Stop();
}
