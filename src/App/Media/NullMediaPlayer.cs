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

    public void Open(string path) { }

    public void Play() { }

    public void Pause() { }

    public void Stop() { }

    public void Seek(TimeSpan t) { }

#pragma warning disable CS0067 // Events are part of the interface contract but never raised by the null object.
    public event EventHandler? PositionChanged;

    public event EventHandler? DurationAvailable;

    public event EventHandler? Ended;

    public event EventHandler<string>? Failed;
#pragma warning restore CS0067
}
