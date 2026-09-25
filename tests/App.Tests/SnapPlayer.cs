using System;
using VideoSplitJoiner.App.Media;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// A player the test makes ready and moves by hand, so a gesture that needs a frame on screen (the snapshot,
/// T-135) can run without a decoder. Promoted from <c>SnapshotProfileThumbnailTests</c> by T-181, whose
/// in-flight tests take a snapshot while a picture re-take is waiting on its grab.
/// </summary>
internal sealed class SnapPlayer : IMediaPlayer
{
    public TimeSpan Position { get; set; }

    public TimeSpan? Duration { get; private set; }

    public bool IsPlaying { get; private set; }

    public double Volume { get; set; } = 1.0;

    public bool IsMuted { get; set; }

    public double SpeedRatio { get; set; } = 1.0;

    public void MakeReady(TimeSpan duration)
    {
        Duration = duration;
        DurationAvailable?.Invoke(this, EventArgs.Empty);
    }

    public void MovePlayheadTo(TimeSpan t)
    {
        Position = t;
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Open(string path)
    {
        IsPlaying = false;
        Duration = null;
        Position = TimeSpan.Zero;
    }

    public void Play() => IsPlaying = true;

    public void Pause() => IsPlaying = false;

    public void Stop() { IsPlaying = false; Position = TimeSpan.Zero; }

    public void Seek(TimeSpan t) => Position = t;

    public void Unload() { Duration = null; IsPlaying = false; Position = TimeSpan.Zero; }

    public void StepFrame(int direction) { }

    public event EventHandler? PositionChanged;

    public event EventHandler? DurationAvailable;

#pragma warning disable CS0067
    public event EventHandler? Seeked;

    public event EventHandler? Ended;

    public event EventHandler<string>? Failed;
#pragma warning restore CS0067
}
