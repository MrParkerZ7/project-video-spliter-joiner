namespace VideoSplitJoiner.App.Media;

/// <summary>
/// Pure geometry helper for the 4K-smoothness work (T-024): given a video source's pixel
/// dimensions and a maximum PREVIEW height, compute the size the on-screen preview should be
/// decoded/rendered at. This drives the FFME <c>scale=W:H</c> video filter so a 4K source is
/// previewed at ~720–1080p, keeping the WPF UI thread unsaturated.
/// </summary>
/// <remarks>
/// This affects ONLY the on-screen preview. The split path never decodes (it is <c>-c copy</c>),
/// so the cut always runs at the source's full resolution regardless of this helper. Kept pure
/// (no I/O, no FFME types) so it is trivially unit-testable headlessly.
/// </remarks>
public static class PreviewScale
{
    /// <summary>
    /// Compute the preview target size for a source of <paramref name="sourceWidth"/> ×
    /// <paramref name="sourceHeight"/>, capped so the target height never exceeds
    /// <paramref name="maxPreviewHeight"/>. Aspect ratio is preserved; the source is NEVER
    /// upscaled (a source already at/below the cap is returned unchanged). Both returned
    /// dimensions are rounded to the nearest EVEN number, because the yuv420p pixel format most
    /// H.264/HEVC sources use requires even width and height (an odd dimension makes the ffmpeg
    /// <c>scale</c> filter fail).
    /// </summary>
    /// <returns>
    /// The (width, height) to preview at. If any input is non-positive (unknown/garbage
    /// dimensions), returns the source size unchanged — the caller then simply skips the scale
    /// filter and lets FFME decode natively.
    /// </returns>
    public static (int Width, int Height) ComputeTarget(int sourceWidth, int sourceHeight, int maxPreviewHeight)
    {
        // Unknown / garbage dimensions → do not attempt to scale. Return input verbatim; the
        // caller treats "target == source" as "no downscale needed".
        if (sourceWidth <= 0 || sourceHeight <= 0 || maxPreviewHeight <= 0)
        {
            return (sourceWidth, sourceHeight);
        }

        // Never upscale: a source at or under the cap is previewed at its own resolution.
        if (sourceHeight <= maxPreviewHeight)
        {
            return (MakeEven(sourceWidth), MakeEven(sourceHeight));
        }

        // Downscale by the height cap, preserving aspect ratio.
        var scale = (double)maxPreviewHeight / sourceHeight;
        var targetWidth = (int)System.Math.Round(sourceWidth * scale);
        var targetHeight = maxPreviewHeight;

        targetWidth = MakeEven(targetWidth);
        targetHeight = MakeEven(targetHeight);

        // Guard the rounding edge: never let rounding push a dimension to 0.
        if (targetWidth < 2)
        {
            targetWidth = 2;
        }

        if (targetHeight < 2)
        {
            targetHeight = 2;
        }

        return (targetWidth, targetHeight);
    }

    /// <summary>
    /// True when the computed preview target is strictly smaller than the source — i.e. a scale
    /// filter is actually worth applying. Equal or larger (the never-upscale / unknown cases)
    /// returns false so the caller can skip the filter entirely.
    /// </summary>
    public static bool ShouldDownscale(int sourceWidth, int sourceHeight, int maxPreviewHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || maxPreviewHeight <= 0)
        {
            return false;
        }

        var (w, h) = ComputeTarget(sourceWidth, sourceHeight, maxPreviewHeight);
        return w < sourceWidth || h < sourceHeight;
    }

    /// <summary>
    /// Build the ffmpeg <c>scale</c> video-filter string for the preview target, or <c>null</c>
    /// when no downscale is needed (source already small, or dimensions unknown). Uses explicit
    /// even width/height computed by <see cref="ComputeTarget"/> so it works with hardware-decoded
    /// frames too (which FFME downloads to CPU before this software filter runs).
    /// </summary>
    public static string? BuildScaleFilter(int sourceWidth, int sourceHeight, int maxPreviewHeight)
    {
        if (!ShouldDownscale(sourceWidth, sourceHeight, maxPreviewHeight))
        {
            return null;
        }

        var (w, h) = ComputeTarget(sourceWidth, sourceHeight, maxPreviewHeight);
        return $"scale={w}:{h}";
    }

    /// <summary>Round to the nearest even integer (rounds down on an odd value).</summary>
    private static int MakeEven(int value) => value - (value % 2);
}
