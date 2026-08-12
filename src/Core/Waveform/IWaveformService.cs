namespace VideoSplitJoiner.Core.Waveform;

/// <summary>
/// UI-free service that extracts a normalized audio <b>peak array</b> from a video (via the ffmpeg CLI)
/// for a scrub-bar audio waveform. Best-effort throughout: <see cref="GetPeaksAsync"/> returns a
/// normalized <c>float[]</c> on success and <c>null</c> on ANY failure OR no-audio — it never throws
/// (a waveform that can't be produced simply hides the band).
/// <para>
/// Core stays UI-free: this returns raw peak data (<c>float[]</c>, values 0..1), never a WPF/visual type.
/// The App layer (T-084) draws the returned peaks into the timeline band itself.
/// </para>
/// </summary>
public interface IWaveformService
{
    /// <summary>
    /// Extract a downsampled mono PCM stream of <paramref name="inputPath"/> and reduce it to a
    /// normalized <b>0..1 peak array</b> of length <paramref name="buckets"/> (max-abs amplitude per
    /// contiguous window) — or <c>null</c> on any failure (missing input, ffmpeg error, cancellation,
    /// I/O) OR when the source has no audio track (empty PCM). Never throws.
    /// <para>
    /// Cached by <c>(inputPath | last-write-time | length)</c>: a repeat call for the same file+bucket
    /// count reuses the peaks WITHOUT re-running ffmpeg. Honors <paramref name="ct"/> — a superseded
    /// request can be cancelled and never clobbers a newer result.
    /// </para>
    /// </summary>
    /// <param name="inputPath">The source media file.</param>
    /// <param name="buckets">The desired peak-array length (waveform columns); &lt;= 0 → <c>null</c>.</param>
    /// <param name="ct">Cancellation token for superseded requests.</param>
    /// <returns>A normalized <c>float[buckets]</c> (0..1), or <c>null</c> on any failure / no-audio.</returns>
    Task<float[]?> GetPeaksAsync(string inputPath, int buckets, CancellationToken ct);

    /// <summary>Best-effort drop of the cached peaks + temp PCM for one input file (missing = no-op, never throws).</summary>
    void Clear(string inputPath);

    /// <summary>Best-effort drop of the whole waveform cache (in-memory + temp root; missing = no-op, never throws).</summary>
    void ClearAll();
}
