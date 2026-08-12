using System.Threading;
using System.Threading.Tasks;
using VideoSplitJoiner.Core.Waveform;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// A no-op <see cref="IWaveformService"/> used when no real waveform source is wired (existing
/// <see cref="SplitViewModel"/> constructions / tests that don't exercise the band). Every extraction
/// resolves to <c>null</c> (no audio → band hidden) and the clear methods do nothing — so the waveform
/// machinery is inert rather than needing null checks throughout <see cref="SplitViewModel"/>. Mirrors
/// <see cref="NullThumbnailService"/>.
/// </summary>
internal sealed class NullWaveformService : IWaveformService
{
    /// <summary>The shared inert instance.</summary>
    public static readonly NullWaveformService Instance = new();

    private NullWaveformService()
    {
    }

    /// <inheritdoc />
    public Task<float[]?> GetPeaksAsync(string inputPath, int buckets, CancellationToken ct)
        => Task.FromResult<float[]?>(null);

    /// <inheritdoc />
    public void Clear(string inputPath)
    {
    }

    /// <inheritdoc />
    public void ClearAll()
    {
    }
}
