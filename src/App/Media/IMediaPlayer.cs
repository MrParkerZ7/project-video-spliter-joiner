using System;

namespace VideoSplitJoiner.App.Media;

/// <summary>
/// A testable abstraction over a video preview player (transport + timeline). The WPF-bound
/// <see cref="MediaElementPlayer"/> is the production implementation; unit tests supply a fake so
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

    /// <summary>Raised as the playhead advances during playback (roughly every ~200ms).</summary>
    event EventHandler PositionChanged;

    /// <summary>Raised once the source's <see cref="Duration"/> becomes known.</summary>
    event EventHandler DurationAvailable;

    /// <summary>Raised when playback reaches the end of the source.</summary>
    event EventHandler Ended;

    /// <summary>Raised when the source cannot be opened/played; carries a human-readable reason.</summary>
    event EventHandler<string> Failed;
}
