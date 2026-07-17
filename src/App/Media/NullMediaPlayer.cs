using System;

namespace VideoSplitJoiner.App.Media;

/// <summary>
/// A do-nothing <see cref="IMediaPlayer"/> (null object). Used as the default player for
/// <see cref="VideoSplitJoiner.App.ViewModels.SplitViewModel"/> so existing constructions (and
/// tests) that don't supply a player keep working — it records nothing, plays nothing, and never
/// raises events. The production composition root replaces it with a <see cref="FfmeMediaPlayer"/>.
/// </summary>
public sealed class NullMediaPlayer : IMediaPlayer
{
    /// <summary>Shared singleton — the null player is stateless.</summary>
    public static readonly NullMediaPlayer Instance = new();

    public TimeSpan Position { get; set; }

    public TimeSpan? Duration => null;

    public bool IsPlaying => false;

    /// <summary>No-op semantics, but stored so bindings read back what they set (default full).</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>No-op semantics, but stored so bindings read back what they set.</summary>
    public bool IsMuted { get; set; }

    /// <summary>No-op semantics, but stored so bindings read back what they set (default normal).</summary>
    public double SpeedRatio { get; set; } = 1.0;

    public void Open(string path) { }

    public void Play() { }

    public void Pause() { }

    public void Stop() { }

    public void Seek(TimeSpan t) { }

    public void StepFrame(int direction) { }

#pragma warning disable CS0067 // Events are part of the interface contract but never raised by the null object.
    public event EventHandler? PositionChanged;

    public event EventHandler? DurationAvailable;

    public event EventHandler? Ended;

    public event EventHandler<string>? Failed;
#pragma warning restore CS0067
}
