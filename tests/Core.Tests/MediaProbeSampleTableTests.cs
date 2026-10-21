using FluentAssertions;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-194 — <see cref="MediaProbe.GetKeyframesAsync"/> with the sample-table path in front of the packet scan: the anchor (two
/// short ffprobe packet windows that check the table at both ends), its comparison rule, every way it can fail, and the T-093
/// guarantees (one scan per file, a caller's cancel never reaches the shared scan, only success is cached) that must hold across
/// it. Driven by fake runners on a crafted MP4 whose table says keyframes at 0, 1 and 2 seconds (<see cref="Mp4Builder"/>), so
/// no ffmpeg is needed; the real ffprobe is in <see cref="IsoBmffKeyframeIndexParityTests"/>.
/// </summary>
public sealed class MediaProbeSampleTableTests : IDisposable
{
    // What ffprobe lists for the crafted file's ends when the table is right (B-frame order: the key frame's packet first).
    private const string HeadAgrees = """
        {"packets":[
          {"pts_time":"0.000000","dts_time":"0.000000","flags":"K__"},
          {"pts_time":"0.500000","dts_time":"0.500000","flags":"___"},
          {"pts_time":"1.000000","dts_time":"1.000000","flags":"K__"}
        ]}
        """;

    private const string TailAgrees = """
        {"packets":[
          {"pts_time":"2.000000","dts_time":"1.900000","flags":"K__"},
          {"pts_time":"1.950000","dts_time":"1.950000","flags":"___"},
          {"pts_time":"2.100000","dts_time":"2.000000","flags":"___"}
        ]}
        """;

    private const string FullScan = """
        {"packets":[
          {"pts_time":"0.000000","flags":"K__"},{"pts_time":"0.500000","flags":"___"},{"pts_time":"1.000000","flags":"K__"},
          {"pts_time":"2.000000","flags":"K__"},{"pts_time":"2.500000","flags":"___"},{"pts_time":"3.000000","flags":"K__"}
        ]}
        """;

    private static readonly TimeSpan[] IndexList = { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) };
    private static readonly TimeSpan[] ScanList = { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) };

    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try
            {
                File.Delete(f);
            }
            catch (IOException)
            {
            }
        }
    }

    private string NewMp4(Action<Mp4Builder>? tweak = null)
    {
        var builder = new Mp4Builder();
        tweak?.Invoke(builder);
        var path = Path.Combine(Path.GetTempPath(), "vsj-anchor-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(path, builder.Build());
        _files.Add(path);
        return path;
    }

    private static AnchorFakeFfprobeRunner Fake(string head = HeadAgrees, string tail = TailAgrees) =>
        new() { HeadJson = head, TailJson = tail, FullScanJson = FullScan, FramesJson = """{"frames":[{"key_frame":1,"pts_time":"0.000000"},{"key_frame":1,"pts_time":"9.000000"}]}""" };

    // ---- The happy path ------------------------------------------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task TableAndBothWindowsAgree_IndexListIsUsed_WithExactlyTwoWindowRuns_AndNoWholeFileScan()
    {
        var fake = Fake();
        var probe = new MediaProbe(fake);
        var path = NewMp4();

        var keyframes = await probe.GetKeyframesAsync(path);

        keyframes.Should().Equal(IndexList);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Index);
        fake.Runs.Should().HaveCount(2, "the only ffprobe runs are the two anchor windows");
        fake.WindowRuns.Should().HaveCount(2);
        fake.FullScanRuns.Should().Be(0);
        fake.Runs.Should().OnlyContain(a => a.Contains("-read_intervals") && a.Contains("-nofind_stream_info") && a.Contains("-show_packets"));
        fake.Runs.Should().OnlyContain(a => a[0] == "-hide_banner", "the args come from FfmpegArgs.ForFfprobe");
        fake.HeadInterval.Should().Be("%1.001");
        fake.TailInterval.Should().Be("2.001%1000000000", "ffprobe's open-ended 'start%' is not deterministic, so the window states an end far past the file");

        // Cached: the second call runs nothing.
        (await probe.GetKeyframesAsync(path)).Should().Equal(IndexList);
        fake.Runs.Should().HaveCount(2);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task TailWindowThatStartsAtTheLastKeyframe_ThoughTheIntervalStartsAMillisecondLater_Agrees()
    {
        // ffprobe seeks BACK to the keyframe at or before the interval start and lists every packet from there.
        var tail = """{"packets":[{"pts_time":"2.000000","dts_time":"1.933333","flags":"K__"},{"pts_time":"2.100000","dts_time":"1.966667","flags":"___"}]}""";
        var probe = new MediaProbe(Fake(tail: tail));
        (await probe.GetKeyframesAsync(NewMp4())).Should().Equal(IndexList);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Index);
    }

    // ---- Disagreement: the packet scan answers --------------------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task TailWindowThatLandsOnTheKeyframeBeforeAndFlagsOnlyIt_Disagrees()
    {
        // The table's last keyframe (2 s) is not one: the seek lands on the keyframe at 1 s and only that is flagged.
        var tail = """{"packets":[{"pts_time":"1.000000","flags":"K__"},{"pts_time":"1.500000","flags":"___"},{"pts_time":"2.000000","flags":"___"}]}""";
        var fake = Fake(tail: tail);
        var probe = new MediaProbe(fake);

        var keyframes = await probe.GetKeyframesAsync(NewMp4());

        keyframes.Should().Equal(ScanList);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Packets);
        fake.FullScanRuns.Should().Be(1);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("tail")]
    [Trait("serves-spec", "SPEC-004")]
    public async Task WindowThatListsNoKeyframe_Disagrees(string which)
    {
        const string noKeyframe = """{"packets":[{"pts_time":"0.500000","flags":"___"}]}""";
        const string noPackets = """{"packets":[]}""";
        foreach (var payload in new[] { noKeyframe, noPackets })
        {
            var fake = which == "head" ? Fake(head: payload) : Fake(tail: payload);
            var probe = new MediaProbe(fake);
            (await probe.GetKeyframesAsync(NewMp4())).Should().Equal(ScanList);
            probe.LastScanPath.Should().Be(KeyframeScanPath.Packets);
        }
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task EveryValueOneFrameLaterThanTheTable_Disagrees()
    {
        var head = """{"packets":[{"pts_time":"0.033333","flags":"K__"},{"pts_time":"1.033333","flags":"K__"}]}""";
        var tail = """{"packets":[{"pts_time":"2.033333","flags":"K__"}]}""";
        var probe = new MediaProbe(Fake(head, tail));
        (await probe.GetKeyframesAsync(NewMp4())).Should().Equal(ScanList);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Packets);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task HeadWindowThatLacksAKeyframeTheTableHasBeforeItsEnd_Disagrees()
    {
        // E.g. a keyframe before an edit list's start that the table lists and ffprobe does not.
        var head = """{"packets":[{"pts_time":"1.000000","flags":"K__"}]}""";
        var probe = new MediaProbe(Fake(head));
        (await probe.GetKeyframesAsync(NewMp4())).Should().Equal(ScanList);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Packets);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task ReaderThatReturnsAListShiftedByOneFrame_IsRefused_AndThePacketListIsWhatGetsCached()
    {
        var fake = Fake();
        var probe = new MediaProbe(fake)
        {
            KeyframeIndexReader = (open, ct) =>
            {
                var real = IsoBmffKeyframeIndex.TryRead(open, ct)!;
                var frame = TimeSpan.FromSeconds(1.0 / 30);
                return real with { Keyframes = real.Keyframes.Select(k => k + frame).ToList() };
            },
        };
        var path = NewMp4();

        (await probe.GetKeyframesAsync(path)).Should().Equal(ScanList);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Packets);
        fake.Runs.Should().HaveCount(3, "two windows, then the whole-file scan");

        (await probe.GetKeyframesAsync(path)).Should().Equal(ScanList, "the packet-scan list is the one cached");
        fake.Runs.Should().HaveCount(3);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task TheSampleTableRead_NeverRunsInTheCallersSynchronizationContext()
    {
        // The Split and Bulk view models start the scan from the WPF UI thread. `await Task.Yield()` in the shared scan
        // hands its continuation back to that context, so a blocking file read placed there froze the window for as long as
        // the read took (a network share, a cold disk). The reader must run on the thread pool.
        var seen = new List<SynchronizationContext?>();
        var probe = new MediaProbe(Fake());
        var inner = probe.KeyframeIndexReader;
        probe.KeyframeIndexReader = (open, ct) =>
        {
            seen.Add(SynchronizationContext.Current);
            return inner(open, ct);
        };
        var path = NewMp4();

        Task<IReadOnlyList<TimeSpan>> scan;
        var caller = new PoolPostingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(caller);
        try
        {
            scan = probe.GetKeyframesAsync(path); // starts the shared scan synchronously, under the caller's context
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        (await scan).Should().Equal(IndexList);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Index);
        seen.Should().ContainSingle().Which.Should().BeNull("the sample table is read on the thread pool, not in the caller's (UI) context");
    }

    /// <summary>A stand-in for the UI thread's dispatcher: it runs what is posted to it on a pool thread with itself as the current context.</summary>
    private sealed class PoolPostingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetSynchronizationContext(this);
                try
                {
                    d(state);
                }
                finally
                {
                    SetSynchronizationContext(null);
                }
            });
    }

    // ---- Failure paths ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("head")]
    [InlineData("tail")]
    [InlineData("both")]
    [Trait("serves-spec", "SPEC-004")]
    public async Task AnchorWindowThatFails_FallsBackToThePacketScan_AndThatListIsCached(string which)
    {
        var fake = Fake();
        if (which is "head" or "both")
        {
            fake.HeadThrows = new FfprobeException(1, new[] { "moov atom not found" });
        }

        if (which is "tail" or "both")
        {
            fake.TailThrows = new FfprobeException(1, new[] { "invalid data" });
        }

        var probe = new MediaProbe(fake);
        var path = NewMp4();

        (await probe.GetKeyframesAsync(path)).Should().Equal(ScanList);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Packets);
        (await probe.GetKeyframesAsync(path)).Should().Equal(ScanList);
        fake.FullScanRuns.Should().Be(1, "cached after the first");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task AnchorWindowWithOutputThatIsNotJson_FallsBackToThePacketScan()
    {
        var probe = new MediaProbe(Fake(head: "this is not json"));
        (await probe.GetKeyframesAsync(NewMp4())).Should().Equal(ScanList);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Packets);
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task AnchorRunnerThrowingOperationCanceled_CachesNothing_AndTheNextCallScansAgainFromAWindow()
    {
        var fake = Fake();
        fake.HeadThrows = new OperationCanceledException();
        fake.Latency = TimeSpan.FromMilliseconds(30);
        var probe = new MediaProbe(fake);
        var path = NewMp4();

        var act = async () => await probe.GetKeyframesAsync(path);
        await act.Should().ThrowAsync<OperationCanceledException>();
        fake.FullScanRuns.Should().Be(0, "a cancellation is not a disagreement: no scan behind it");

        fake.HeadThrows = null;
        fake.Runs.Clear();
        (await probe.GetKeyframesAsync(path)).Should().Equal(IndexList);
        fake.Runs.Should().NotBeEmpty();
        fake.Runs[0].Should().Contain("-read_intervals", "the first run of the next call is a window again: nothing was cached");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task CallerThatCancelsWhileAnAnchorWindowRuns_CancelsItselfOnly_TheSharedScanCachesTheIndexList_ASecondCallerGetsIt()
    {
        var fake = Fake();
        fake.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new MediaProbe(fake);
        var path = NewMp4();
        using var cts = new CancellationTokenSource();

        var callA = probe.GetKeyframesAsync(path, cts.Token);
        var callB = probe.GetKeyframesAsync(path);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        var actA = async () => await callA;
        await actA.Should().ThrowAsync<OperationCanceledException>("A's own await is cancelled");

        fake.Gate.SetResult();
        (await callB).Should().Equal(IndexList, "the shared scan went on and B receives its list");
        probe.LastScanPath.Should().Be(KeyframeScanPath.Index);
        fake.Runs.Should().HaveCount(2, "one scan: two windows, shared by both callers");

        (await probe.GetKeyframesAsync(path)).Should().Equal(IndexList);
        fake.Runs.Should().HaveCount(2, "cached");
    }

    // ---- The reader declines; the file is never in the way --------------------------------------------------------------

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task ReaderThatDeclines_RunsTodaysPacketScan_WithNoWindows()
    {
        var fake = Fake();
        var probe = new MediaProbe(fake);
        var path = NewMp4(b => b.MoovExtras.Add(Mp4Builder.Box("mvex", new byte[8]))); // fragmented: declined

        (await probe.GetKeyframesAsync(path)).Should().Equal(ScanList);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Packets);
        fake.Runs.Should().HaveCount(1);
        fake.Runs[0].Should().NotContain("-read_intervals");
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [Trait("serves-spec", "SPEC-004")]
    public async Task OpenThatFails_IsADecline_ThePacketScanRuns_NothingThrows(string failure)
    {
        var fake = Fake();
        var probe = new MediaProbe(fake)
        {
            OpenKeyframeSource = _ => failure == "io"
                ? throw new IOException("The process cannot access the file because it is being used by another process.")
                : throw new UnauthorizedAccessException("Access to the path is denied."),
        };

        (await probe.GetKeyframesAsync(NewMp4())).Should().Equal(ScanList);
        probe.LastScanPath.Should().Be(KeyframeScanPath.Packets);
        fake.WindowRuns.Should().BeEmpty();
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task WithTheReadersStreamStillOpen_TheFileCanBeRenamedAndDeleted()
    {
        var path = NewMp4();
        var renamed = path + ".renamed";
        _files.Add(renamed);
        var renamedWhileOpen = false;
        var deletedWhileOpen = false;
        var probe = new MediaProbe(Fake())
        {
            KeyframeIndexReader = (open, _) =>
            {
                using var stream = open(); // the stream MediaProbe opens for the reader, kept open while the file is moved
                File.Move(path, renamed);
                renamedWhileOpen = true;
                File.Delete(renamed);
                deletedWhileOpen = true;
                return null;
            },
        };

        var act = async () => await probe.GetKeyframesAsync(path);
        await act.Should().NotThrowAsync();
        renamedWhileOpen.Should().BeTrue("FileShare.ReadWrite | Delete lets the file be renamed under the reader");
        deletedWhileOpen.Should().BeTrue("... and deleted");
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public async Task ReaderOpensTheFileReadOnly_AndSharesItWithWritersAndDeleters()
    {
        var path = NewMp4();
        FileStream? seen = null;
        var probe = new MediaProbe(Fake())
        {
            KeyframeIndexReader = (open, _) =>
            {
                seen = (FileStream)open();
                try
                {
                    seen.CanWrite.Should().BeFalse();
                    // A writer may open the file while the reader's stream is open.
                    using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                    writer.Length.Should().BeGreaterThan(0);
                }
                finally
                {
                    seen.Dispose();
                }

                return null;
            },
        };

        await probe.GetKeyframesAsync(path);
        seen.Should().NotBeNull();
    }

    // ---- The comparison rule on its own ---------------------------------------------------------------------------------

    private static MediaProbe.PacketWindow Window(double? first, params double[] keyframes) =>
        new(first is { } f ? TimeSpan.FromSeconds(f) : null, keyframes.Select(TimeSpan.FromSeconds).ToList());

    private static IReadOnlyList<TimeSpan> Index(params double[] seconds) => seconds.Select(TimeSpan.FromSeconds).ToList();

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Rule_HeadListsFirstTwo_TailListsLast_Agree() =>
        MediaProbe.AnchorAgrees(Index(0, 1, 2, 3), Window(0, 0, 1), Window(3, 3)).Should().BeTrue();

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Rule_TailStartsAtTheLastKeyframe_OutputBeginsThere_Agree() =>
        MediaProbe.AnchorAgrees(Index(0, 1, 2), Window(0, 0, 1), Window(2, 2)).Should().BeTrue();

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Rule_TailSeeksBackFurtherAndListsSeveralKeyframes_StillAgrees_WhenTheyAreTheTables() =>
        MediaProbe.AnchorAgrees(Index(0, 1, 2, 3), Window(0, 0, 1), Window(1, 1, 2, 3)).Should().BeTrue();

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Rule_TailLandsOnThePreviousKeyframe_FlagsOnlyIt_Disagree() =>
        MediaProbe.AnchorAgrees(Index(0, 1, 2), Window(0, 0, 1), Window(1, 1)).Should().BeFalse();

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Rule_TailListsAKeyframeTheTableLacks_Disagree() =>
        MediaProbe.AnchorAgrees(Index(0, 1, 2), Window(0, 0, 1), Window(2, 2, 2.5)).Should().BeFalse();

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Rule_HeadListsAKeyframeTheTableLacks_Disagree() =>
        MediaProbe.AnchorAgrees(Index(0, 1, 2), Window(0, 0, 0.5, 1), Window(2, 2)).Should().BeFalse();

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Rule_HeadMissingTheFirstKeyframe_Disagree() =>
        MediaProbe.AnchorAgrees(Index(0, 1, 2), Window(0.5, 1), Window(2, 2)).Should().BeFalse();

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Rule_AWindowWithoutAKeyframe_Disagree()
    {
        MediaProbe.AnchorAgrees(Index(0, 1, 2), Window(0), Window(2, 2)).Should().BeFalse();
        MediaProbe.AnchorAgrees(Index(0, 1, 2), Window(0, 0, 1), Window(2)).Should().BeFalse();
        MediaProbe.AnchorAgrees(Index(0, 1, 2), Window(null), Window(null)).Should().BeFalse();
    }

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Rule_TailWithoutAFirstPacketTime_Disagree() =>
        MediaProbe.AnchorAgrees(Index(0, 1, 2), Window(0, 0, 1), Window(null, 2)).Should().BeFalse();

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Rule_EveryValueOneFrameLater_Disagree() =>
        MediaProbe.AnchorAgrees(Index(0, 1, 2), Window(0.033333, 0.033333, 1.033333), Window(2.033333, 2.033333)).Should().BeFalse();

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Rule_TheTimesAreCompared_TimeSpanExact_NotApproximately() =>
        MediaProbe.AnchorAgrees(Index(0, 1, 2), Window(0, 0, 1.000001), Window(2, 2)).Should().BeFalse();

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Rule_FewerThanTwoKeyframesInTheTable_Disagree() =>
        MediaProbe.AnchorAgrees(Index(0), Window(0, 0), Window(0, 0)).Should().BeFalse();

    [Fact]
    [Trait("serves-spec", "SPEC-004")]
    public void Rule_ANegativeFirstKeyframe_PreRoll_IsListedByTheHeadWindow() =>
        MediaProbe.AnchorAgrees(Index(-0.5, 0.5, 1.5), Window(-0.5, -0.5, 0.5), Window(1.5, 1.5)).Should().BeTrue();
}

/// <summary>
/// A fake <see cref="IFfprobeRunner"/> that tells the three queries apart — the HEAD anchor window (<c>-read_intervals %…</c>), the
/// TAIL window (<c>-read_intervals …%…</c>) and the whole-file packet scan — answers each with canned JSON, records every run, and
/// can throw for a window or hold every window behind a gate.
/// </summary>
internal sealed class AnchorFakeFfprobeRunner : IFfprobeRunner
{
    private int _fullScanRuns;

    public string? HeadJson { get; set; }

    public string? TailJson { get; set; }

    public string? FullScanJson { get; set; }

    public string? FramesJson { get; set; }

    public Exception? HeadThrows { get; set; }

    public Exception? TailThrows { get; set; }

    /// <summary>When set, every window waits for it before answering.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>
    /// How long a window takes to answer. A test whose scan ends in a FAILURE sets it, as a real ffprobe run always takes
    /// milliseconds: a scan that fails in microseconds can end before GetOrAdd has registered its task, and the finally block
    /// then removes nothing (the entry it should remove is not there yet), leaving the failed task registered.
    /// </summary>
    public TimeSpan Latency { get; set; }

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<string[]> Runs { get; } = new();

    public IEnumerable<string[]> WindowRuns => Runs.ToList().Where(a => a.Contains("-read_intervals"));

    public int FullScanRuns => Volatile.Read(ref _fullScanRuns);

    public string? HeadInterval => WindowRuns.Select(Interval).FirstOrDefault(i => i.StartsWith('%'));

    public string? TailInterval => WindowRuns.Select(Interval).FirstOrDefault(i => !i.StartsWith('%'));

    private static string Interval(string[] args) => args[Array.IndexOf(args, "-read_intervals") + 1];

    public async Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default)
    {
        var list = args.ToList().ToArray();
        lock (Runs)
        {
            Runs.Add(list);
        }

        if (list.Contains("-read_intervals"))
        {
            Entered.TrySetResult();
            if (Latency > TimeSpan.Zero)
            {
                await Task.Delay(Latency).ConfigureAwait(false);
            }

            if (Gate is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }

            var head = Interval(list).StartsWith('%');
            var thrown = head ? HeadThrows : TailThrows;
            if (thrown is not null)
            {
                throw thrown;
            }

            return (head ? HeadJson : TailJson) ?? """{"packets":[]}""";
        }

        if (list.Contains("-show_packets"))
        {
            Interlocked.Increment(ref _fullScanRuns);
            return FullScanJson ?? throw new FfprobeException(1, new[] { "simulated packet-query failure" });
        }

        return FramesJson ?? throw new FfprobeException(1, new[] { "simulated frame-query failure" });
    }
}
