using System;
using System.Collections.Generic;
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
    private double _volume = 1.0;
    private bool _isMuted;
    private double _speedRatio = 1.0;

    // True while we are applying a player-driven position update (PositionChanged / Stop / Seek echo)
    // — used to suppress the Position setter's Seek so a playback tick never re-seeks the player.
    private bool _suppressSeek;

    // ---- Scrub pop-back guard (T-033) -------------------------------------------------------
    // A user seek to T is async in FFME: the seek to T is dispatched but playback keeps ticking, so
    // PositionChanged echoes arrive with STALE positions before the seek lands. Without this guard
    // those echoes yank the slider/display off T ("pop-back"). While _seeking is true we PIN the
    // display at _seekTarget and ignore any echo that isn't near the target; the hold is released
    // deterministically by the player's Seeked event, or defensively by a tolerance match or a
    // bounded number of non-matching ticks so the slider can never freeze permanently.

    /// <summary>How close an echo must be to <see cref="_seekTarget"/> to count as "the seek landed".</summary>
    private static readonly TimeSpan SeekTolerance = TimeSpan.FromMilliseconds(250);

    /// <summary>Max non-matching echoes to swallow before releasing the hold anyway (anti-freeze backstop).</summary>
    private const int MaxHeldTicks = 12;

    private bool _seeking;
    private TimeSpan _seekTarget;
    private int _heldTicks;

    // True while the user is actively dragging the scrub thumb (PlayerView Thumb.DragStarted..
    // DragCompleted). While dragging we suppress position echoes entirely; the seek fires on release.
    private bool _isUserScrubbing;

    /// <summary>Create the player VM over <paramref name="player"/> and subscribe to its events.</summary>
    public PlayerViewModel(IMediaPlayer player)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));

        _player.PositionChanged += OnPositionChanged;
        _player.Seeked += OnSeeked;
        _player.DurationAvailable += OnDurationAvailable;
        _player.Ended += OnEnded;
        _player.Failed += OnFailed;

        PlayPauseCommand = new RelayCommand(_ => PlayPause(), _ => IsReady);
        StopCommand = new RelayCommand(_ => Stop());
        MuteCommand = new RelayCommand(_ => ToggleMute());

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
                    // User-driven set (bound slider): arm the seek-target hold, then seek.
                    BeginSeek(clamped);
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

    // ---- Audio (volume + mute) --------------------------------------------------------------

    /// <summary>
    /// Output volume 0..1 (default 1.0). The setter writes <see cref="IMediaPlayer.Volume"/>. This is
    /// the value the slider binds to; it is kept intact across a mute/unmute cycle (muting toggles the
    /// player's <see cref="IMediaPlayer.IsMuted"/>, not this slider value), so unmuting restores the
    /// exact level the slider showed.
    /// </summary>
    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = value < 0 ? 0 : value > 1 ? 1 : value;
            if (SetProperty(ref _volume, clamped))
            {
                _player.Volume = clamped;
            }
        }
    }

    /// <summary>
    /// True while audio is muted. Toggled by <see cref="MuteCommand"/>; the setter writes
    /// <see cref="IMediaPlayer.IsMuted"/>. Muting does NOT alter <see cref="Volume"/> (the slider keeps
    /// its value), so unmute needs no separate "restore" — the slider level was never lost.
    /// </summary>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetProperty(ref _isMuted, value))
            {
                _player.IsMuted = value;
                OnPropertyChanged(nameof(MuteLabel));
            }
        }
    }

    /// <summary>Button caption/glyph reflecting the mute state.</summary>
    public string MuteLabel => IsMuted ? "Unmute" : "Mute";

    // ---- Playback speed ---------------------------------------------------------------------

    /// <summary>
    /// Playback speed multiplier (default 1.0). The setter writes <see cref="IMediaPlayer.SpeedRatio"/>.
    /// Bound to the speed ComboBox's SelectedItem against <see cref="SpeedPresets"/>.
    /// </summary>
    public double SpeedRatio
    {
        get => _speedRatio;
        set
        {
            if (SetProperty(ref _speedRatio, value))
            {
                _player.SpeedRatio = value;
                OnPropertyChanged(nameof(SpeedText));
            }
        }
    }

    /// <summary>Selectable speed presets for the ComboBox (0.25×…2×).</summary>
    public IReadOnlyList<double> SpeedPresets { get; } = new[] { 0.25, 0.5, 1.0, 1.5, 2.0 };

    /// <summary>Current speed as a short label, e.g. <c>"1x"</c> / <c>"1.5x"</c>.</summary>
    public string SpeedText =>
        string.Create(CultureInfo.InvariantCulture, $"{_speedRatio.ToString("0.##", CultureInfo.InvariantCulture)}x");

    // ---- Commands ---------------------------------------------------------------------------

    /// <summary>Toggle play/pause (guarded by <see cref="IsReady"/>).</summary>
    public RelayCommand PlayPauseCommand { get; }

    /// <summary>Stop playback and rewind to the start.</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>Toggle mute on/off (writes the player's IsMuted; leaves the slider Volume intact).</summary>
    public RelayCommand MuteCommand { get; }

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
        // Reset audio/speed to defaults for the new source (writes through to the player too).
        Volume = 1.0;
        IsMuted = false;
        SpeedRatio = 1.0;
        ClearSeekHold();
        _isUserScrubbing = false;
        SetPositionFromPlayer(TimeSpan.Zero);
        _player.Open(path);
    }

    /// <summary>
    /// Unload the current source and reset the preview back to empty (T-047). Blanks the player
    /// surface via <see cref="IMediaPlayer.Unload"/> and clears all preview state: duration (→ not
    /// ready), position, playing flag, and the preview-failed banner, plus any in-flight seek/scrub
    /// hold. The command guards that depend on <see cref="IsReady"/> re-raise via the
    /// <see cref="Duration"/> setter. Restores audio/speed to their defaults for the next load.
    /// </summary>
    public void Unload()
    {
        _player.Unload();

        PreviewFailed = false;
        PreviewFailedReason = null;
        IsPlaying = false;
        Duration = null; // → IsReady false; raises the play/scrub command guards
        // Reset audio/speed to defaults (writes through to the player too), mirroring Open.
        Volume = 1.0;
        IsMuted = false;
        SpeedRatio = 1.0;
        ClearSeekHold();
        _isUserScrubbing = false;
        SetPositionFromPlayer(TimeSpan.Zero);
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
        ClearSeekHold();
        SetPositionFromPlayer(TimeSpan.Zero);
    }

    /// <summary>
    /// Toggle mute. Flips <see cref="IsMuted"/> (which writes the player's IsMuted); the slider
    /// <see cref="Volume"/> value is deliberately left untouched so unmute restores the prior level.
    /// </summary>
    public void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>
    /// Seek the player to <paramref name="t"/> (used by the timeline / T-013 capture and by every
    /// skip/jump/frame-step jog). Pins the display at the target and arms the seek-target hold so a
    /// stale playback echo can't pop the playhead back off the requested position (T-033).
    /// </summary>
    public void Scrub(TimeSpan t) => BeginSeek(Clamp(t));

    /// <summary>
    /// Signal that the user has grabbed the scrub thumb. While scrubbing, position echoes are
    /// suppressed entirely (the slider follows the drag, not playback); the actual seek fires on
    /// <see cref="EndUserScrub"/>. Wired from the view's <c>Thumb.DragStarted</c> (T-033).
    /// </summary>
    public void BeginUserScrub() => _isUserScrubbing = true;

    /// <summary>
    /// Signal that the user released the scrub thumb at <paramref name="finalSeconds"/> (the slider's
    /// final value). Clears the scrubbing flag and seeks to the final position with the seek-target
    /// hold armed. Wired from the view's <c>Thumb.DragCompleted</c> (T-033).
    /// </summary>
    public void EndUserScrub(double finalSeconds)
    {
        _isUserScrubbing = false;
        // Setting Position (not _suppressSeek) arms BeginSeek and issues the seek to the final value.
        Position = TimeSpan.FromSeconds(finalSeconds);
    }

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

    private void OnPositionChanged(object? sender, EventArgs e)
    {
        var incoming = _player.Position;

        // (1) While the user is dragging the thumb, the slider owns the display — ignore echoes
        //     entirely so playback ticks don't fight the drag.
        if (_isUserScrubbing)
        {
            return;
        }

        // (2) While a seek-target hold is armed, keep the display pinned at the target until the
        //     seek lands (or the anti-freeze backstop trips), so stale echoes don't pop it back.
        if (_seeking)
        {
            var delta = incoming - _seekTarget;
            if (delta < TimeSpan.Zero)
            {
                delta = delta.Negate();
            }

            if (delta <= SeekTolerance)
            {
                // The seek landed — release the hold and apply this (on-target) update normally.
                ClearSeekHold();
                SetPositionFromPlayer(incoming);
                return;
            }

            // Not on target yet. Swallow the stale echo (display stays pinned at _seekTarget) unless
            // we've swallowed too many — then release so a never-exactly-matching echo can't freeze
            // the slider forever, and let this echo through as the resumed playback position.
            if (++_heldTicks >= MaxHeldTicks)
            {
                ClearSeekHold();
                SetPositionFromPlayer(incoming);
            }

            return;
        }

        // (3) Normal playback echo.
        SetPositionFromPlayer(incoming);
    }

    /// <summary>
    /// The player finished the async seek (FFME's continuation). Release the seek-target hold
    /// deterministically and snap the display to the landed position (T-033).
    /// </summary>
    private void OnSeeked(object? sender, EventArgs e)
    {
        if (!_seeking)
        {
            return;
        }

        ClearSeekHold();
        // Snap to the player's settled position (normally == _seekTarget); a player-driven set so it
        // does not re-seek.
        SetPositionFromPlayer(_player.Position);
    }

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

    /// <summary>
    /// Arm the seek-target hold and issue the seek. Pins the display at <paramref name="target"/>
    /// (a suppressed set, so it does not itself re-seek), records the target, and calls the player's
    /// async <see cref="IMediaPlayer.Seek"/>. Until the seek settles, <see cref="OnPositionChanged"/>
    /// ignores off-target echoes so the playhead can't pop back (T-033).
    /// </summary>
    private void BeginSeek(TimeSpan target)
    {
        _seekTarget = target;
        _seeking = true;
        _heldTicks = 0;

        // Pin the visible position at the target now (suppressed = no re-seek), so the slider shows
        // where the user asked to go even before the async seek lands.
        SetPositionFromPlayer(target);

        _player.Seek(target);
    }

    /// <summary>Release the seek-target hold (T-033).</summary>
    private void ClearSeekHold()
    {
        _seeking = false;
        _heldTicks = 0;
    }

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
