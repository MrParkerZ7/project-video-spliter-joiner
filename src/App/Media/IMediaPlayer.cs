using System;

namespace VideoSplitJoiner.App.Media;

/// <summary>
/// A testable abstraction over a video preview player (transport + timeline). The FFME-backed
/// <see cref="FfmeMediaPlayer"/> is the production implementation; unit tests supply a fake so
/// <see cref="VideoSplitJoiner.App.ViewModels.PlayerViewModel"/> logic can be exercised with no
/// GUI and no real playback.
/// </summary>
/// <remarks>
/// The player is stateful: <see cref="Open"/> loads a source, <see cref="DurationAvailable"/> fires
/// once its duration is known, <see cref="PositionChanged"/> ticks while playing, <see cref="Ended"/>
/// fires at the end, and <see cref="Failed"/> fires (with a reason) when the source cannot be played.
/// </remarks>
public interface IMediaPlayer
{
    /// <summary>Current playhead position. Setting it seeks (clamped by the implementation).</summary>
    TimeSpan Position { get; set; }

    /// <summary>Total duration once known (after <see cref="DurationAvailable"/>), else null.</summary>
    TimeSpan? Duration { get; }

    /// <summary>True while playback is running.</summary>
    bool IsPlaying { get; }

    /// <summary>Output volume in 0..1 (0 = silent, 1 = full). Setting it applies immediately.</summary>
    double Volume { get; set; }

    /// <summary>True while audio output is muted (independent of <see cref="Volume"/>).</summary>
    bool IsMuted { get; set; }

    /// <summary>Playback speed multiplier (1.0 = normal, 0.5 = half, 2.0 = double).</summary>
    double SpeedRatio { get; set; }

    /// <summary>Load <paramref name="path"/> as the current source (does not auto-play).</summary>
    void Open(string path);

    /// <summary>Start (or resume) playback.</summary>
    void Play();

    /// <summary>Pause playback, keeping the current position.</summary>
    void Pause();

    /// <summary>Stop playback and reset the position to the start.</summary>
    void Stop();

    /// <summary>Seek to <paramref name="t"/> (clamped to 0..Duration by the implementation).</summary>
    void Seek(TimeSpan t);

    /// <summary>
    /// Step the playhead exactly one frame in <paramref name="direction"/> (+1 forward, −1 back).
    /// A paused operation — the implementation pauses playback first if it is running. Values other
    /// than ±1 are treated by sign; <c>0</c> is a no-op.
    /// </summary>
    void StepFrame(int direction);

    /// <summary>Raised as the playhead advances during playback (roughly every ~200ms).</summary>
    event EventHandler PositionChanged;

    /// <summary>
    /// Raised once a <see cref="Seek"/> operation has completed (the async seek settled). Lets the VM
    /// clear its seek-target hold deterministically rather than relying on a position tolerance — see
    /// <see cref="VideoSplitJoiner.App.ViewModels.PlayerViewModel"/>'s scrub pop-back guard (T-033).
    /// </summary>
    event EventHandler Seeked;

    /// <summary>Raised once the source's <see cref="Duration"/> becomes known.</summary>
    event EventHandler DurationAvailable;

    /// <summary>Raised when playback reaches the end of the source.</summary>
    event EventHandler Ended;

    /// <summary>Raised when the source cannot be opened/played; carries a human-readable reason.</summary>
    event EventHandler<string> Failed;
}
