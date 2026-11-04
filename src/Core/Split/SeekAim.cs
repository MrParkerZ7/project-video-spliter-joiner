using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.Core.Split;

/// <summary>
/// The aim of an input seek to a keyframe, checked with the demuxer before it is used (T-237, SPEC-001 I75).
///
/// <para>The aim is the keyframe's own time whenever a seek aimed there lands on it, which is what it was before T-237 and what the
/// copy path of every constant-frame-rate file does. When the seek lands on the keyframe BEFORE it (an MP4 or QuickTime file with
/// B-frames and irregular frame times: measured with ffmpeg n7.1, 0.035 to 0.1 s past the keyframe is needed on a phone-style file),
/// the aim moves later until the seek lands on the keyframe, and the first aim that does is used. A first version of this change aimed
/// at the middle of the GOP by a rule on times; a review found a screen-recording-style file with idle stretches where the keyframe's
/// own aim was right and the middle landed one GOP late. What a seek lands on is the demuxer's to say, so it is asked
/// (<see cref="ISeekLandingProbe"/>, one ffprobe packet read, about 0.05 to 0.1 s), and nothing is assumed from the times.</para>
///
/// <para>A seek lands on the last keyframe whose index time is not after the aim, so the landing never moves back as the aim moves
/// later: an aim lands early (before the keyframe) up to some point, on the keyframe for a stretch, then on a later keyframe. The search
/// uses that: a short ladder of aims first (most files stop at the first or second), then, when the ladder ran out of room before it
/// found the stretch (a window between two ladder steps, or a cut that ends soon after its keyframe), the last aim in reach, and a
/// bisection between the last aim that landed early and the first that landed late. A second review found 14 of 41 end trims called
/// unresolved by the ladder alone that a 5 ms sweep showed to have an aim that lands, which is why the search does not stop at the ladder.</para>
///
/// <para>Without a probe, or when the probe cannot say, the aim is the keyframe: today's behaviour, unverified.</para>
/// </summary>
internal static class SeekAim
{
    /// <summary>How close the landed keyframe's time must be to the wanted one (the keyframe list's times are rounded to a microsecond).</summary>
    internal static readonly TimeSpan LandingTolerance = TimeSpan.FromMilliseconds(2);

    /// <summary>An aim stays at least this far before the next keyframe (or the cut's end), which it must not reach.</summary>
    internal static readonly TimeSpan Margin = TimeSpan.FromMilliseconds(5);

    /// <summary>The aims tried first after the keyframe's own, as seconds past it: the measured gap is 0.035 to 0.1 s, so most files stop at the second.</summary>
    internal static readonly IReadOnlyList<double> LadderSeconds = new[] { 0.04, 0.08, 0.15, 0.3, 0.6, 1.2 };

    /// <summary>The most halvings of the stretch between the last early aim and the first late one (a stretch of 1.2 s is cut below the margin in 8).</summary>
    internal const int MaxBisections = 10;

    internal enum Outcome
    {
        /// <summary>No probe, or it could not say: the keyframe's own time, as before T-237.</summary>
        Unverified,

        /// <summary>A seek aimed at the keyframe lands on it.</summary>
        OnKeyframe,

        /// <summary>A seek aimed at the keyframe lands earlier; a later aim lands on it and is the answer.</summary>
        Anchored,

        /// <summary>The probe said, and no aim in reach lands on the keyframe: the aim is the keyframe's own and the seek will land on another keyframe.</summary>
        Unresolved,
    }

    internal readonly record struct Choice(TimeSpan Aim, Outcome Outcome);

    /// <summary>
    /// The aim for a seek to <paramref name="keyframe"/>. <paramref name="upper"/> is what the aim may not reach: the next keyframe, the end
    /// of the file, or the end of the cut, whichever comes first.
    /// </summary>
    internal static async Task<Choice> ChooseAsync(
        ISeekLandingProbe? probe, string path, TimeSpan keyframe, TimeSpan upper, CancellationToken ct)
    {
        if (probe is null)
        {
            return new Choice(keyframe, Outcome.Unverified);
        }

        var landed = await LandingAsync(probe, path, keyframe, ct).ConfigureAwait(false);
        if (landed is null)
        {
            return new Choice(keyframe, Outcome.Unverified);
        }

        if (IsOn(landed.Value, keyframe))
        {
            return new Choice(keyframe, Outcome.OnKeyframe);
        }

        if (IsLate(landed.Value, keyframe))
        {
            // The landing never moves back as the aim moves later, so a later aim cannot land on this keyframe either.
            return new Choice(keyframe, Outcome.Unresolved);
        }

        var limit = upper - Margin;
        var early = keyframe;   // the latest aim known to land before the keyframe
        TimeSpan? late = null;  // the earliest aim known to land after it

        foreach (var step in LadderSeconds)
        {
            var aim = keyframe + TimeSpan.FromSeconds(step);
            if (aim > limit)
            {
                break;
            }

            landed = await LandingAsync(probe, path, aim, ct).ConfigureAwait(false);
            if (landed is null)
            {
                return new Choice(keyframe, Outcome.Unverified);
            }

            if (IsOn(landed.Value, keyframe))
            {
                return new Choice(aim, Outcome.Anchored);
            }

            if (IsLate(landed.Value, keyframe))
            {
                late = aim;
                break;
            }

            early = aim;
        }

        if (late is null)
        {
            // The ladder ran out of room, not of aims: the last aim in reach is the best chance of the stretch, and when it still lands
            // early no aim does.
            if (limit - early <= Margin)
            {
                return new Choice(keyframe, Outcome.Unresolved);
            }

            landed = await LandingAsync(probe, path, limit, ct).ConfigureAwait(false);
            if (landed is null)
            {
                return new Choice(keyframe, Outcome.Unverified);
            }

            if (IsOn(landed.Value, keyframe))
            {
                return new Choice(limit, Outcome.Anchored);
            }

            if (!IsLate(landed.Value, keyframe))
            {
                return new Choice(keyframe, Outcome.Unresolved);
            }

            late = limit;
        }

        // The keyframe's stretch, if there is one, is between the last aim that landed early and the first that landed late.
        var hi = late.Value;
        for (var i = 0; i < MaxBisections && hi - early > Margin; i++)
        {
            var mid = early + ((hi - early) / 2);
            landed = await LandingAsync(probe, path, mid, ct).ConfigureAwait(false);
            if (landed is null)
            {
                return new Choice(keyframe, Outcome.Unverified);
            }

            if (IsOn(landed.Value, keyframe))
            {
                return new Choice(mid, Outcome.Anchored);
            }

            if (IsLate(landed.Value, keyframe))
            {
                hi = mid;
            }
            else
            {
                early = mid;
            }
        }

        return new Choice(keyframe, Outcome.Unresolved);
    }

    private static bool IsOn(TimeSpan landed, TimeSpan keyframe) => (landed - keyframe).Duration() <= LandingTolerance;

    private static bool IsLate(TimeSpan landed, TimeSpan keyframe) => landed - keyframe > LandingTolerance;

    /// <summary>A probe that throws (anything but a cancellation) cannot say, which is the same as null.</summary>
    private static async Task<TimeSpan?> LandingAsync(ISeekLandingProbe probe, string path, TimeSpan aim, CancellationToken ct)
    {
        try
        {
            return await probe.GetSeekLandingAsync(path, aim, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
