using System;
using System.Threading;
using System.Threading.Tasks;

namespace VideoSplitJoiner.App.Media;

/// <summary>
/// Sequences a media element's Close→Open lifecycle so an Open is never issued while a prior Close
/// (or Open) is still in flight (T-080). FFME's <c>Open</c>/<c>Close</c> are async commands; issuing
/// <c>Open</c> while the element is <c>IsClosing</c> / <c>IsOpening</c> / <c>IsChanging</c> is a known
/// NATIVE crash spot (an AccessViolation that bypasses managed exception handlers). The reproduction
/// is: split → Clear (fire-and-forget Close) → immediately load a new video (Open) before the close
/// has settled.
///
/// <para>This class is deliberately WPF-free: the element's transitional state is read through
/// <see cref="IReopenTarget"/> and the inter-poll wait through an injected delay, so the sequencing
/// logic is fully unit-testable headlessly. <see cref="FfmeMediaPlayer"/> adapts the real FFME
/// <c>MediaElement</c> to <see cref="IReopenTarget"/>.</para>
/// </summary>
public sealed class MediaReopenGuard
{
    /// <summary>How long to keep polling toward a settled state before giving up (open-unsafe).</summary>
    public static readonly TimeSpan DefaultSettleTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Interval between transitional-state polls.</summary>
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(30);

    private readonly IReopenTarget _target;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _settleTimeout;
    private readonly TimeSpan _poll;

    // Monotonically increasing lifecycle generation. Every RequestOpen / NotifySuperseded bumps it;
    // a settle wait drops the moment its generation is no longer the newest (a newer Open/Unload won).
    private long _generation;

    /// <summary>Create a guard over <paramref name="target"/> with production timing.</summary>
    public MediaReopenGuard(IReopenTarget target)
        : this(target, DefaultSettleTimeout, DefaultPoll, (d, ct) => Task.Delay(d, ct), () => DateTime.UtcNow)
    {
    }

    /// <summary>
    /// Testable ctor: <paramref name="delay"/> is the inter-poll wait seam and <paramref name="now"/>
    /// the clock, so the settle loop is deterministic with no wall-clock waits.
    /// </summary>
    public MediaReopenGuard(
        IReopenTarget target,
        TimeSpan settleTimeout,
        TimeSpan poll,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<DateTime> now)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _settleTimeout = settleTimeout > TimeSpan.Zero ? settleTimeout : DefaultSettleTimeout;
        _poll = poll > TimeSpan.Zero ? poll : DefaultPoll;
    }

    /// <summary>The current lifecycle generation (test/diagnostic hook).</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// Register a new Open request and return its lifecycle generation token. Any earlier pending
    /// open (an in-flight <see cref="WaitUntilReopenableAsync"/>) is thereby superseded and will drop.
    /// </summary>
    public long RequestOpen() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// Register a teardown (Unload/Close) — bumps the generation so any pending open is superseded and
    /// drops without opening against a closing element.
    /// </summary>
    public void NotifySuperseded() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// Await the element out of any transitional (closing/opening/changing) state for the open request
    /// identified by <paramref name="generation"/>. Returns:
    /// <list type="bullet">
    /// <item><see cref="ReopenDecision.Open"/> — settled and still the newest request → safe to open;</item>
    /// <item><see cref="ReopenDecision.Superseded"/> — a newer Open/Unload arrived while waiting → drop;</item>
    /// <item><see cref="ReopenDecision.Timeout"/> — stayed transitional past the settle timeout → open-unsafe.</item>
    /// </list>
    /// Never throws: a fault reading the element's state is treated as "still transitional" (keep waiting).
    /// </summary>
    public async Task<ReopenDecision> WaitUntilReopenableAsync(long generation, CancellationToken ct = default)
    {
        var deadline = _now() + _settleTimeout;

        while (true)
        {
            if (Interlocked.Read(ref _generation) != generation || _target.IsDetached)
            {
                return ReopenDecision.Superseded;
            }

            if (SafeIsReopenable())
            {
                // Re-check the generation AFTER confirming settled — a supersede that raced the last
                // poll must still win (drop rather than open against a newer request's teardown).
                return Interlocked.Read(ref _generation) == generation
                    ? ReopenDecision.Open
                    : ReopenDecision.Superseded;
            }

            if (_now() >= deadline)
            {
                return ReopenDecision.Timeout;
            }

            try
            {
                await _delay(_poll, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return ReopenDecision.Superseded;
            }
        }
    }

    /// <summary>
    /// Read <see cref="IReopenTarget.IsReopenable"/> defensively: a throw (e.g. a torn-down element)
    /// is treated as "not reopenable yet" so we keep waiting rather than surface a crash.
    /// </summary>
    private bool SafeIsReopenable()
    {
        try
        {
            return _target.IsReopenable;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>The outcome of waiting for a media element to become re-openable (T-080).</summary>
public enum ReopenDecision
{
    /// <summary>The element settled and this is still the newest request — issue the Open.</summary>
    Open,

    /// <summary>A newer Open/Unload superseded this request while waiting — drop it.</summary>
    Superseded,

    /// <summary>The element stayed transitional past the settle timeout — opening is unsafe.</summary>
    Timeout,
}

/// <summary>
/// The element-state seam the <see cref="MediaReopenGuard"/> reads (T-080). Keeps the guard WPF-free
/// and unit-testable: <see cref="FfmeMediaPlayer"/> implements it over the real FFME
/// <c>MediaElement</c>; tests supply a fake that scripts the transitional→settled transition.
/// </summary>
public interface IReopenTarget
{
    /// <summary>
    /// True when the element is safe to (re)open: not mid-close, mid-open, or changing components.
    /// </summary>
    bool IsReopenable { get; }

    /// <summary>True when there is no element to open (detached) — the guard stops waiting.</summary>
    bool IsDetached { get; }
}
