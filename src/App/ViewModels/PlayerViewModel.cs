using System;
using System.Globalization;
using VideoSplitJoiner.App.Media;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// View model for the in-app video preview player (T-012). Sits over an <see cref="IMediaPlayer"/>
/// (the FFME-backed <see cref="FfmeMediaPlayer"/> in production, a fake in tests) and exposes a
/// WPF-free transport surface: observable <see cref="Position"/> / <see cref="Duration"/> /
/// <see cref="IsPlaying"/> / <see cref="IsReady"/>, formatted <see cref="PositionText"/> /
/// <see cref="DurationText"/>, a <see cref="PreviewFailed"/> banner flag, and
/// <see cref="PlayPauseCommand"/> / <see cref="StopCommand"/>. A slider can two-way-bind to
/// <see cref="Position"/> to scrub; a re-entrancy flag stops player-driven position updates from
/// looping back into a seek. Deliberately holds no WPF types so it is fully unit-testable.
/// </summary>
public sealed class PlayerViewModel : ObservableObject
{
    private readonly IMediaPlayer _player;

    private TimeSpan _position;
    private TimeSpan? _duration;
    private bool _isPlaying;
    private bool _previewFailed;
    private string? _previewFailedReason;

    // True while we are applying a player-driven position update (PositionChanged / Stop / Seek echo)
    // — used to suppress the Position setter's Seek so a playback tick never re-seeks the player.
    private bool _suppressSeek;

    /// <summary>Create the player VM over <paramref name="player"/> and subscribe to its events.</summary>
    public PlayerViewModel(IMediaPlayer player)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));

        _player.PositionChanged += OnPositionChanged;
        _player.DurationAvailable += OnDurationAvailable;
        _player.Ended += OnEnded;
        _player.Failed += OnFailed;

        PlayPauseCommand = new RelayCommand(_ => PlayPause(), _ => IsReady);
        StopCommand = new RelayCommand(_ => Stop());

        // Relative jog: the command parameter is a number of SECONDS (a double, or a string XAML
        // supplies as CommandParameter="10" / "-5"). All jog/step/jump commands are gated on IsReady.
        SkipCommand = new RelayCommand(p => SkipBy(TimeSpan.FromSeconds(ParseSeconds(p))), _ => IsReady);
        JumpToStartCommand = new RelayCommand(_ => Scrub(TimeSpan.Zero), _ => IsReady);
        JumpToEndCommand = new RelayCommand(_ => JumpToEnd(), _ => IsReady);
        StepForwardCommand = new RelayCommand(_ => StepFrame(+1), _ => IsReady);
        StepBackCommand = new RelayCommand(_ => StepFrame(-1), _ => IsReady);
    }

    // ---- State ------------------------------------------------------------------------------

    /// <summary>
    /// Current playhead position. The setter is the scrub seam: a user-driven set (e.g. a bound
    /// slider) calls <see cref="IMediaPlayer.Seek"/>, while a player-driven update (via
    /// <see cref="OnPositionChanged"/>) sets it under the suppression flag so it does NOT re-seek.
    /// </summary>
    public TimeSpan Position
    {
        get => _position;
        set
        {
            var clamped = Clamp(value);
            if (SetProperty(ref _position, clamped))
            {
                OnPropertyChanged(nameof(PositionText));
                OnPropertyChanged(nameof(PositionSeconds));
                if (!_suppressSeek)
                {
                    _player.Seek(clamped);
                }
            }
        }
    }

    /// <summary>Total media duration once known, else null.</summary>
    public TimeSpan? Duration
    {
        get => _duration;
        private set
        {
            if (SetProperty(ref _duration, value))
            {
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(DurationSeconds));
                OnPropertyChanged(nameof(IsReady));
                PlayPauseCommand.RaiseCanExecuteChanged();
                SkipCommand.RaiseCanExecuteChanged();
                JumpToStartCommand.RaiseCanExecuteChanged();
                JumpToEndCommand.RaiseCanExecuteChanged();
                StepForwardCommand.RaiseCanExecuteChanged();
                StepBackCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The underlying player, exposed so the view's code-behind can attach the FFME
    /// <c>MediaElement</c> when the concrete player is a <see cref="FfmeMediaPlayer"/>. VM logic
    /// never touches WPF types — this is only the attach seam.
    /// </summary>
    public IMediaPlayer PlayerControl => _player;

    /// <summary>True while the underlying player is playing.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseLabel));
            }
        }
    }

    /// <summary>True once the duration is known — gates play/pause and scrubbing.</summary>
    public bool IsReady => _duration is not null;

    /// <summary>Set when the source failed to open/play; drives the "preview unavailable" banner.</summary>
    public bool PreviewFailed
    {
        get => _previewFailed;
        private set => SetProperty(ref _previewFailed, value);
    }

    /// <summary>The reason the preview failed (surfaced under the banner), or null.</summary>
    public string? PreviewFailedReason
    {
        get => _previewFailedReason;
        private set => SetProperty(ref _previewFailedReason, value);
    }

    /// <summary>Current position as <c>mm:ss.f</c>.</summary>
    public string PositionText => FormatClock(_position);

    /// <summary>Total duration as <c>mm:ss.f</c> (empty until known).</summary>
    public string DurationText => _duration is { } d ? FormatClock(d) : "--:--.-";

    /// <summary>Position in total seconds — a slider-friendly double two-way bound to <see cref="Position"/>.</summary>
    public double PositionSeconds
    {
        get => _position.TotalSeconds;
        set => Position = TimeSpan.FromSeconds(value);
    }

    /// <summary>Duration in total seconds — the slider's Maximum (0 until known).</summary>
    public double DurationSeconds => _duration?.TotalSeconds ?? 0d;

    /// <summary>Button caption reflecting the transport state.</summary>
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    // ---- Commands ---------------------------------------------------------------------------

    /// <summary>Toggle play/pause (guarded by <see cref="IsReady"/>).</summary>
    public RelayCommand PlayPauseCommand { get; }

    /// <summary>Stop playback and rewind to the start.</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>
    /// Jog the playhead by a relative number of seconds. The command parameter is the delta in
    /// seconds (a <see cref="double"/>, or a string XAML binds via <c>CommandParameter</c> such as
    /// <c>"10"</c> / <c>"-5"</c>); positive = forward, negative = back. Guarded by <see cref="IsReady"/>.
    /// </summary>
    public RelayCommand SkipCommand { get; }

    /// <summary>Jump the playhead to the very start (00:00). Guarded by <see cref="IsReady"/>.</summary>
    public RelayCommand JumpToStartCommand { get; }

    /// <summary>Jump the playhead to the very end (the full duration). Guarded by <see cref="IsReady"/>.</summary>
    public RelayCommand JumpToEndCommand { get; }

    /// <summary>Step one frame forward (paused operation). Guarded by <see cref="IsReady"/>.</summary>
    public RelayCommand StepForwardCommand { get; }

    /// <summary>Step one frame backward (paused operation). Guarded by <see cref="IsReady"/>.</summary>
    public RelayCommand StepBackCommand { get; }

    // ---- Actions ----------------------------------------------------------------------------

    /// <summary>
    /// Load <paramref name="path"/> into the player and reset preview state. Exposed for T-013's
    /// Load wiring so opening a file in the Split screen also opens it in the preview.
    /// </summary>
    public void Open(string path)
    {
        PreviewFailed = false;
        PreviewFailedReason = null;
        Duration = null;
        IsPlaying = false;
        SetPositionFromPlayer(TimeSpan.Zero);
        _player.Open(path);
    }

    /// <summary>Toggle transport: play when paused, pause when playing. No-op until ready.</summary>
    public void PlayPause()
    {
        if (!IsReady)
        {
            return;
        }

        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
        }
        else
        {
            _player.Play();
            IsPlaying = true;
        }
    }

    /// <summary>Stop playback, rewind to start, and clear the playing flag.</summary>
    public void Stop()
    {
        _player.Stop();
        IsPlaying = false;
        SetPositionFromPlayer(TimeSpan.Zero);
    }

    /// <summary>Seek the player to <paramref name="t"/> (used by the timeline / T-013 capture).</summary>
    public void Scrub(TimeSpan t) => _player.Seek(Clamp(t));

    /// <summary>
    /// Jog the playhead by <paramref name="delta"/> relative to the current position, clamped to
    /// <c>0..Duration</c>, then seek there (via <see cref="Scrub"/>). No-op until <see cref="IsReady"/>.
    /// Used to nudge the playhead onto the exact split point before "Set cut at playhead".
    /// </summary>
    public void SkipBy(TimeSpan delta)
    {
        if (!IsReady)
        {
            return;
        }

        var upper = _duration ?? _position;
        Scrub(ClampTo(_position + delta, TimeSpan.Zero, upper));
    }

    /// <summary>Jump the playhead to the media's end (full duration). No-op until ready.</summary>
    public void JumpToEnd()
    {
        if (!IsReady || _duration is not { } d)
        {
            return;
        }

        Scrub(d);
    }

    /// <summary>
    /// Step the underlying player exactly one frame in <paramref name="direction"/> (+1 / −1).
    /// No-op until <see cref="IsReady"/>. FFME reports the resulting position via PositionChanged.
    /// </summary>
    public void StepFrame(int direction)
    {
        if (!IsReady)
        {
            return;
        }

        _player.StepFrame(direction);
    }

    // ---- Player events ----------------------------------------------------------------------

    private void OnPositionChanged(object? sender, EventArgs e) => SetPositionFromPlayer(_player.Position);

    private void OnDurationAvailable(object? sender, EventArgs e)
    {
        Duration = _player.Duration;
        PreviewFailed = false;
        PreviewFailedReason = null;
    }

    private void OnEnded(object? sender, EventArgs e) => IsPlaying = false;

    private void OnFailed(object? sender, string reason)
    {
        IsPlaying = false;
        PreviewFailed = true;
        PreviewFailedReason = string.IsNullOrWhiteSpace(reason)
            ? "The video could not be played."
            : reason;
    }

    // ---- Helpers ----------------------------------------------------------------------------

    /// <summary>Apply a player-originated position without re-seeking (breaks the feedback loop).</summary>
    private void SetPositionFromPlayer(TimeSpan value)
    {
        _suppressSeek = true;
        try
        {
            Position = value;
        }
        finally
        {
            _suppressSeek = false;
        }
    }

    private TimeSpan Clamp(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return _duration is { } d && t > d ? d : t;
    }

    /// <summary>Clamp <paramref name="t"/> into the inclusive <paramref name="lo"/>..<paramref name="hi"/> range.</summary>
    private static TimeSpan ClampTo(TimeSpan t, TimeSpan lo, TimeSpan hi)
    {
        if (t < lo)
        {
            return lo;
        }

        return t > hi ? hi : t;
    }

    /// <summary>
    /// Parse a <see cref="SkipCommand"/> parameter (a <see cref="double"/>, an <see cref="int"/>,
    /// or a string such as <c>"10"</c> / <c>"-5"</c>) into a seconds value. Unparseable = 0 (no-op).
    /// </summary>
    private static double ParseSeconds(object? parameter) => parameter switch
    {
        double d => d,
        int i => i,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
        _ => 0d,
    };

    /// <summary>Format a time as <c>mm:ss.f</c> (or <c>h:mm:ss.f</c> past an hour).</summary>
    private static string FormatClock(TimeSpan t)
    {
        var a = t < TimeSpan.Zero ? t.Negate() : t;
        return a.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)a.TotalHours}:{a.Minutes:00}:{a.Seconds:00}.{a.Milliseconds / 100}")
            : string.Create(CultureInfo.InvariantCulture, $"{a.Minutes:00}:{a.Seconds:00}.{a.Milliseconds / 100}");
    }
}
