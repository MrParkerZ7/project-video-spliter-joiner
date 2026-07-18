using System;
using System.Threading;
using System.Threading.Tasks;
using VideoSplitJoiner.Core.Thumbnails;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// A no-op <see cref="IThumbnailService"/> used when the hover preview has no real service wired
/// (existing <see cref="PlayerViewModel"/> constructions / tests that don't exercise thumbnails). Every
/// grab resolves to <c>null</c> (no image) and the clear methods do nothing — so the hover machinery is
/// inert rather than needing null checks throughout <see cref="ThumbnailPreviewViewModel"/>.
/// </summary>
internal sealed class NullThumbnailService : IThumbnailService
{
    /// <summary>The shared inert instance.</summary>
    public static readonly NullThumbnailService Instance = new();

    private NullThumbnailService()
    {
    }

    /// <inheritdoc />
    public Task<string?> GetThumbnailAsync(string inputPath, TimeSpan time, int width, CancellationToken ct)
        => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public void Clear(string inputPath)
    {
    }

    /// <inheritdoc />
    public void ClearAll()
    {
    }
}
