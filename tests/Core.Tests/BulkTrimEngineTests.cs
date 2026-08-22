using FluentAssertions;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Split;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Behavioural tests for the D-004 Bulk Cut Core orchestrator <see cref="BulkTrimEngine"/>:
/// sequential loop, failure isolation, cancel + no-partial-moved, collision policy (incl.
/// source-safety), batch disk pre-flight, no-op skip, progress rollup, and ledger completeness.
/// Uses a binary-free <see cref="FakeSplitEngine"/> + a probe-free <see cref="FakeRequestBuilder"/>
/// + a deterministic <see cref="FakeDiskSpaceProbe"/> — no real ffmpeg, no real disk measurement.
/// </summary>
public class BulkTrimEngineTests
{
    private static readonly IDiskSpaceProbe RoomyDisk = new FakeDiskSpaceProbe(long.MaxValue);

    // --- fixtures -----------------------------------------------------------------------------

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vsj-bulk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>A real placeholder source file + its default <c>_trimmed</c> desired output path.</summary>
    private static BulkTrimItem MakeItem(string dir, string name, TimeSpan? outro = null, object? tag = null)
    {
        var input = Path.Combine(dir, name + ".mp4");
        File.WriteAllText(input, "source-bytes");
        var desired = Path.Combine(dir, name + "_trimmed.mp4");
        return new BulkTrimItem(input, TimeSpan.FromSeconds(2), outro ?? TimeSpan.FromSeconds(8), desired, tag);
    }

    private static BulkTrimEngine Engine(FakeSplitEngine split, FakeRequestBuilder builder, IDiskSpaceProbe? disk = null) =>
        new(split, builder, disk ?? RoomyDisk);

    /// <summary>A source file of an exact byte length + its default <c>_trimmed</c> desired output path.</summary>
    private static BulkTrimItem MakeSizedItem(string dir, string name, long sizeBytes)
    {
        var input = Path.Combine(dir, name + ".mp4");
        File.WriteAllBytes(input, new byte[sizeBytes]);
        var desired = Path.Combine(dir, name + "_trimmed.mp4");
        return new BulkTrimItem(input, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), desired);
    }

    // --- required-fail: row-2 throws is isolated ----------------------------------------------

    [Fact]
    public async Task Batch_Row2Throws_Rows1And3Done_Outcome_CompletedWithFailures()
    {
        var dir = NewDir();
        try
        {
            var a = MakeItem(dir, "a");
            var b = MakeItem(dir, "b");
            var c = MakeItem(dir, "c");
            var split = new FakeSplitEngine(behaviorByName: new() { ["b.mp4"] = FakeSplitEngine.Behavior.ThrowSplit });
            var engine = Engine(split, new FakeRequestBuilder());

            var result = await engine.RunAsync(new[] { a, b, c }, new BulkTrimOptions());

            result.Outcome.Should().Be(BatchOutcome.CompletedWithFailures);
            result.Items.Select(r => r.Outcome).Should()
                .Equal(ItemOutcome.Done, ItemOutcome.Failed, ItemOutcome.Done);
            result.DoneCount.Should().Be(2);
            result.FailedCount.Should().Be(1);
            result.Items[1].Error.Should().NotBeNull();
            result.FailedItems.Should().ContainSingle().Which.Item.Should().Be(b);
            split.CallCount.Should().Be(3, "every row is attempted — one failure does not abort the batch");
        }
        finally { Cleanup(dir); }
    }

    // --- cancel mid-batch ---------------------------------------------------------------------

    [Fact]
    public async Task Batch_Cancel_DuringRow2_Row1Kept_Row2Cancelled_Row3NotStarted_NoPartialMoved()
    {
        var dir = NewDir();
        try
        {
            var a = MakeItem(dir, "a");
            var b = MakeItem(dir, "b");
            var c = MakeItem(dir, "c");
            var cts = new CancellationTokenSource();
            var split = new FakeSplitEngine(
                behaviorByName: new() { ["b.mp4"] = FakeSplitEngine.Behavior.Cancel },
                cancelSource: cts);
            var engine = Engine(split, new FakeRequestBuilder());

            var result = await engine.RunAsync(new[] { a, b, c }, new BulkTrimOptions(), progress: null, cts.Token);

            result.Outcome.Should().Be(BatchOutcome.Cancelled);
            result.Items.Select(r => r.Outcome).Should()
                .Equal(ItemOutcome.Done, ItemOutcome.Cancelled, ItemOutcome.NotStarted);

            // Row 1's output is a complete, kept file; row 2's output was NEVER moved into place.
            File.Exists(Path.Combine(dir, "a_trimmed.mp4")).Should().BeTrue("a done row is a complete file, kept");
            File.Exists(Path.Combine(dir, "b_trimmed.mp4")).Should().BeFalse("a cancelled row must leave no partial output");
            File.Exists(Path.Combine(dir, "c_trimmed.mp4")).Should().BeFalse("a not-started row never ran");
            split.CallCount.Should().Be(2, "row 3 is never dispatched after the cancel");
        }
        finally { Cleanup(dir); }
    }

    // --- collision policy ---------------------------------------------------------------------

    [Fact]
    public async Task Collision_AutoSuffix_ExistingTrimmed_ResolvesTo_trimmed_2()
    {
        var dir = NewDir();
        try
        {
            var a = MakeItem(dir, "clip");
            File.WriteAllText(Path.Combine(dir, "clip_trimmed.mp4"), "already here"); // force a collision.

            var split = new FakeSplitEngine();
            var engine = Engine(split, new FakeRequestBuilder());

            var result = await engine.RunAsync(new[] { a }, new BulkTrimOptions(CollisionPolicy.AutoSuffix));

            result.Items[0].Outcome.Should().Be(ItemOutcome.Done);
            Path.GetFileName(result.Items[0].OutputPath!).Should().Be("clip_trimmed_2.mp4");
            split.Received[0].NamingPattern.Should().Be("clip_trimmed_2.mp4");
            File.Exists(Path.Combine(dir, "clip_trimmed_2.mp4")).Should().BeTrue();
            File.ReadAllText(Path.Combine(dir, "clip_trimmed.mp4")).Should().Be("already here", "the existing file is untouched");
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Collision_Skip_ExistingOutput_ItemSkipped_NoEngineCall()
    {
        var dir = NewDir();
        try
        {
            var a = MakeItem(dir, "clip");
            File.WriteAllText(Path.Combine(dir, "clip_trimmed.mp4"), "already here");

            var split = new FakeSplitEngine();
            var engine = Engine(split, new FakeRequestBuilder());

            var result = await engine.RunAsync(new[] { a }, new BulkTrimOptions(CollisionPolicy.Skip));

            result.Items[0].Outcome.Should().Be(ItemOutcome.Skipped);
            result.SkippedCount.Should().Be(1);
            split.CallCount.Should().Be(0, "a skipped row must never reach the engine");
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Collision_Overwrite_PassesOverwriteTrue_KeepsBaseName()
    {
        var dir = NewDir();
        try
        {
            var a = MakeItem(dir, "clip");
            File.WriteAllText(Path.Combine(dir, "clip_trimmed.mp4"), "already here");

            var split = new FakeSplitEngine();
            var engine = Engine(split, new FakeRequestBuilder());

            var result = await engine.RunAsync(new[] { a }, new BulkTrimOptions(CollisionPolicy.Overwrite));

            result.Items[0].Outcome.Should().Be(ItemOutcome.Done);
            split.Received[0].Overwrite.Should().BeTrue("Overwrite policy must set SplitRequest.Overwrite");
            split.Received[0].NamingPattern.Should().Be("clip_trimmed.mp4", "the base name is kept under Overwrite");
            Path.GetFileName(result.Items[0].OutputPath!).Should().Be("clip_trimmed.mp4");
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Collision_NeverTargetsSourcePath()
    {
        var dir = NewDir();
        try
        {
            // Pathological: the desired output IS the source. Even under Overwrite the source must
            // never be a write target — the runner forces AutoSuffix and does NOT overwrite.
            var input = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(input, "original");
            var item = new BulkTrimItem(input, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), input);

            var split = new FakeSplitEngine();
            var engine = Engine(split, new FakeRequestBuilder());

            var result = await engine.RunAsync(new[] { item }, new BulkTrimOptions(CollisionPolicy.Overwrite));

            result.Items[0].Outcome.Should().Be(ItemOutcome.Done);
            var outFull = Path.GetFullPath(result.Items[0].OutputPath!);
            outFull.Should().NotBe(Path.GetFullPath(input), "the source is never a write target");
            Path.GetFileName(outFull).Should().Be("clip_2.mp4");
            split.Received[0].Overwrite.Should().BeFalse("forcing AutoSuffix off the source must not carry Overwrite");
            File.ReadAllText(input).Should().Be("original", "the source file is untouched");
        }
        finally { Cleanup(dir); }
    }

    // --- disk pre-flight ----------------------------------------------------------------------

    [Fact]
    public async Task Preflight_Shortfall_BlocksBatch_ZeroEngineCalls_Outcome_Blocked()
    {
        var dir = NewDir();
        try
        {
            var a = MakeItem(dir, "a");
            var b = MakeItem(dir, "b");
            var split = new FakeSplitEngine();
            var engine = Engine(split, new FakeRequestBuilder(), new FakeDiskSpaceProbe(1000)); // 1000 bytes free ≪ 16 MB.

            var result = await engine.RunAsync(new[] { a, b }, new BulkTrimOptions());

            result.Outcome.Should().Be(BatchOutcome.Blocked);
            result.Items.Should().OnlyContain(r => r.Outcome == ItemOutcome.NotStarted);
            result.Items.Should().OnlyContain(r => r.Error!.Category == ErrorCategory.DiskFull);
            split.CallCount.Should().Be(0, "a blocked batch runs zero ffmpeg");
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Preflight_UnmeasurableDrive_SkipsCheck_BatchRuns()
    {
        var dir = NewDir();
        try
        {
            var a = MakeItem(dir, "a");
            var split = new FakeSplitEngine();
            var engine = Engine(split, new FakeRequestBuilder(), new FakeDiskSpaceProbe(null)); // unmeasurable.

            var result = await engine.RunAsync(new[] { a }, new BulkTrimOptions());

            result.Outcome.Should().Be(BatchOutcome.Completed, "an unmeasurable drive skips the pre-flight, never blocks");
            result.Items[0].Outcome.Should().Be(ItemOutcome.Done);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task MidRun_DiskFull_IsolatedPerRow_BatchContinues()
    {
        var dir = NewDir();
        try
        {
            var a = MakeItem(dir, "a");
            var b = MakeItem(dir, "b");
            var c = MakeItem(dir, "c");
            // Row b throws a mapped ENOSPC — the mapper keys DiskFull on the stderr signature.
            var split = new FakeSplitEngine(
                behaviorByName: new() { ["b.mp4"] = FakeSplitEngine.Behavior.ThrowSplit },
                throwStdErr: "No space left on device");
            var engine = Engine(split, new FakeRequestBuilder());

            var result = await engine.RunAsync(new[] { a, b, c }, new BulkTrimOptions());

            result.Outcome.Should().Be(BatchOutcome.CompletedWithFailures);
            result.Items.Select(r => r.Outcome).Should()
                .Equal(ItemOutcome.Done, ItemOutcome.Failed, ItemOutcome.Done);
            result.Items[1].Error!.Category.Should().Be(ErrorCategory.DiskFull, "a mid-run ENOSPC is isolated to its row");
            split.CallCount.Should().Be(3);
        }
        finally { Cleanup(dir); }
    }

    // --- no-op skip ---------------------------------------------------------------------------

    [Fact]
    public async Task Batch_NoOpTrim_BuilderSignalsNoOp_ItemSkipped()
    {
        var dir = NewDir();
        try
        {
            var a = MakeItem(dir, "a");
            var b = MakeItem(dir, "b");
            var builder = new FakeRequestBuilder();
            builder.NoOpInputs.Add("b.mp4"); // ResolveKeptIndex would throw → NoOpTrimException.
            var split = new FakeSplitEngine();
            var engine = Engine(split, builder);

            var result = await engine.RunAsync(new[] { a, b }, new BulkTrimOptions());

            result.Items.Select(r => r.Outcome).Should().Equal(ItemOutcome.Done, ItemOutcome.Skipped);
            result.Outcome.Should().Be(BatchOutcome.CompletedWithFailures);
            split.CallCount.Should().Be(1, "a no-op row never reaches the engine");
        }
        finally { Cleanup(dir); }
    }

    // --- ledger + warnings + progress ---------------------------------------------------------

    [Fact]
    public async Task Ledger_HasOneEntryPerItem_InInputOrder()
    {
        var dir = NewDir();
        try
        {
            var items = Enumerable.Range(0, 4).Select(i => MakeItem(dir, "f" + i, tag: i)).ToList();
            var split = new FakeSplitEngine();
            var engine = Engine(split, new FakeRequestBuilder());

            var result = await engine.RunAsync(items, new BulkTrimOptions());

            result.Items.Should().HaveCount(items.Count);
            result.Items.Select(r => r.Item.Tag).Should().Equal(0, 1, 2, 3);
            for (var i = 0; i < items.Count; i++)
            {
                result.Items[i].Item.Should().Be(items[i], "the ledger preserves input order + identity");
            }
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Done_Row_Surfaces_SplitResult_Warnings()
    {
        var dir = NewDir();
        try
        {
            var a = MakeItem(dir, "clip");
            var split = new FakeSplitEngine(successWarnings: new[] { "coarse GOP — cuts may move ~1s" });
            var engine = Engine(split, new FakeRequestBuilder());

            var result = await engine.RunAsync(new[] { a }, new BulkTrimOptions());

            result.Items[0].Outcome.Should().Be(ItemOutcome.Done);
            result.Items[0].Warnings.Should().ContainSingle().Which.Should().Contain("coarse GOP");
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Progress_OverallFraction_MonotonicAndReachesOne()
    {
        var dir = NewDir();
        try
        {
            var items = new[] { MakeItem(dir, "a"), MakeItem(dir, "b"), MakeItem(dir, "c") };
            var split = new FakeSplitEngine();
            var engine = Engine(split, new FakeRequestBuilder());
            var progress = new RecordingProgress<BulkTrimProgress>();

            await engine.RunAsync(items, new BulkTrimOptions(), progress);

            progress.Reports.Should().NotBeEmpty();
            progress.Reports[0].Phase.Should().Be(BulkTrimPhase.Preflight, "the first sample reports the pre-flight phase");
            var overalls = progress.Reports.Select(r => r.OverallFraction).ToList();
            overalls.Should().BeInAscendingOrder("overall progress is monotonic non-decreasing");
            overalls[^1].Should().Be(1.0, "overall progress reaches 1.0 on completion");
        }
        finally { Cleanup(dir); }
    }

    // --- empty / all-skipped ------------------------------------------------------------------

    [Fact]
    public async Task Empty_Batch_IsNoOp_ReturnsCompleted()
    {
        var split = new FakeSplitEngine();
        var engine = Engine(split, new FakeRequestBuilder());

        var result = await engine.RunAsync(Array.Empty<BulkTrimItem>(), new BulkTrimOptions());

        result.Outcome.Should().Be(BatchOutcome.Completed);
        result.Items.Should().BeEmpty();
        split.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task AllSkipped_Batch_ZeroEngineCalls_CompletedWithFailures()
    {
        var dir = NewDir();
        try
        {
            var a = MakeItem(dir, "a");
            var b = MakeItem(dir, "b");
            File.WriteAllText(Path.Combine(dir, "a_trimmed.mp4"), "x");
            File.WriteAllText(Path.Combine(dir, "b_trimmed.mp4"), "x");

            var split = new FakeSplitEngine();
            var engine = Engine(split, new FakeRequestBuilder());

            var result = await engine.RunAsync(new[] { a, b }, new BulkTrimOptions(CollisionPolicy.Skip));

            result.Items.Should().OnlyContain(r => r.Outcome == ItemOutcome.Skipped);
            result.SkippedCount.Should().Be(2);
            result.Outcome.Should().Be(BatchOutcome.CompletedWithFailures);
            split.CallCount.Should().Be(0);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task NullItems_IsNoOp_ReturnsCompleted()
    {
        var split = new FakeSplitEngine();
        var engine = Engine(split, new FakeRequestBuilder());

        var result = await engine.RunAsync(null!, null!);

        result.Outcome.Should().Be(BatchOutcome.Completed);
        result.Items.Should().BeEmpty();
    }

    // --- todo-automate gap coverage (SPEC-002) ---------------------------------------------------

    // SPEC-002#I23 — a collision-resolution exception is isolated to its own row (Failed) and never
    // aborts the batch. A DesiredOutputPath that Path.GetFullPath cannot resolve (embedded null char)
    // throws inside ResolveCollision's pre-resolve loop; the surrounding row still runs.
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task Collision_ResolutionThrows_RowFailed_BatchContinues()
    {
        var dir = NewDir();
        try
        {
            // A null character makes Path.GetFullPath(DesiredOutputPath) throw ArgumentException,
            // caught by the pre-resolve catch block → this row is Failed.
            var badInput = Path.Combine(dir, "bad.mp4");
            File.WriteAllText(badInput, "source-bytes");
            var bad = new BulkTrimItem(
                badInput, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), "bad" + (char)0 + "_trimmed.mp4");

            var good = MakeItem(dir, "good");

            var split = new FakeSplitEngine();
            var engine = Engine(split, new FakeRequestBuilder());

            var result = await engine.RunAsync(new[] { bad, good }, new BulkTrimOptions());

            result.Outcome.Should().Be(BatchOutcome.CompletedWithFailures);
            result.Items[0].Outcome.Should().Be(ItemOutcome.Failed, "the unresolvable output path fails its own row");
            result.Items[0].Error.Should().NotBeNull();
            result.Items[1].Outcome.Should().Be(ItemOutcome.Done, "the surrounding row still runs");
            split.CallCount.Should().Be(1, "only the healthy row reaches the engine");
        }
        finally { Cleanup(dir); }
    }

    // SPEC-002#I26 — the disk pre-flight per-root size estimate EXCLUDES rows already decided at
    // pre-resolve (collision-Skipped). With a tight free-bytes value that fits ONLY the runnable row's
    // source (not both), the batch is NOT Blocked and the runnable row runs.
    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task Preflight_ExcludesPreSkippedRow_FromSizeEstimate()
    {
        var dir = NewDir();
        try
        {
            const long oneMb = 1024 * 1024;
            const long margin = 16L * 1024 * 1024; // BulkTrimEngine.PreflightMarginBytes

            // 'skip' already has its output on disk → Skip policy pre-decides it (Skipped) so its size
            // must NOT count toward required space. 'run' is the only runnable row.
            var skip = MakeSizedItem(dir, "skip", oneMb);
            File.WriteAllText(Path.Combine(dir, "skip_trimmed.mp4"), "already here");
            var run = MakeSizedItem(dir, "run", oneMb);

            // Free = 17.5 MB: enough for margin + ONE source (17 MB), NOT for margin + both (18 MB).
            var free = margin + oneMb + (oneMb / 2);
            var engine = Engine(new FakeSplitEngine(), new FakeRequestBuilder(), new FakeDiskSpaceProbe(free));

            var result = await engine.RunAsync(new[] { skip, run }, new BulkTrimOptions(CollisionPolicy.Skip));

            result.Outcome.Should().NotBe(BatchOutcome.Blocked, "the pre-skipped row's size must be excluded from the estimate");
            var runResult = result.Items.Single(r => ReferenceEquals(r.Item, run));
            runResult.Outcome.Should().Be(ItemOutcome.Done, "the runnable row still runs within the tight budget");
            var skipResult = result.Items.Single(r => ReferenceEquals(r.Item, skip));
            skipResult.Outcome.Should().Be(ItemOutcome.Skipped);
        }
        finally { Cleanup(dir); }
    }
}

// --- test doubles -----------------------------------------------------------------------------

/// <summary>Captures every progress report synchronously, in order.</summary>
internal sealed class RecordingProgress<T> : IProgress<T>
{
    public List<T> Reports { get; } = new();

    public void Report(T value) => Reports.Add(value);
}

/// <summary>Deterministic disk-space probe: a fixed free-bytes value (or null = unmeasurable).</summary>
internal sealed class FakeDiskSpaceProbe : IDiskSpaceProbe
{
    private readonly long? _free;

    public FakeDiskSpaceProbe(long? free) => _free = free;

    public long? GetAvailableFreeBytes(string driveRoot) => _free;
}

/// <summary>
/// Probe-free <see cref="IBulkTrimRequestBuilder"/>: echoes a canned single-kept-segment request
/// whose <c>OutputDir</c> + literal <c>NamingPattern</c> resolve to the runner's collision-resolved
/// output path (so <see cref="FakeSplitEngine"/> writes exactly there). Optionally signals a no-op
/// trim per input file name.
/// </summary>
internal sealed class FakeRequestBuilder : IBulkTrimRequestBuilder
{
    /// <summary>Input file names (e.g. <c>"b.mp4"</c>) that resolve to a no-op trim.</summary>
    public HashSet<string> NoOpInputs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(BulkTrimItem Item, string Effective, bool Overwrite)> Calls { get; } = new();

    public Task<SplitRequest> BuildAsync(BulkTrimItem item, string effectiveOutputPath, bool overwrite, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add((item, effectiveOutputPath, overwrite));

        if (NoOpInputs.Contains(Path.GetFileName(item.InputPath)))
        {
            throw new NoOpTrimException($"'{item.InputPath}' resolves to a no-op trim.");
        }

        var full = Path.GetFullPath(effectiveOutputPath);
        var outputDir = Path.GetDirectoryName(full)!;
        var namingPattern = Path.GetFileName(full);
        IReadOnlyList<TimeSpan> cutPoints = item.OutroStart is { } outro
            ? new[] { item.IntroEnd, outro }
            : new[] { item.IntroEnd };

        return Task.FromResult(new SplitRequest(
            item.InputPath, cutPoints, outputDir, namingPattern, overwrite, new[] { 2 }));
    }
}

/// <summary>
/// Binary-free <see cref="ISplitEngine"/> scripted per input file name: succeed (materialize the
/// resolved output + report 0.5→1.0), throw a designated <see cref="SplitException"/>, or cancel a
/// token and throw <see cref="OperationCanceledException"/>. Records every received request.
/// </summary>
internal sealed class FakeSplitEngine : ISplitEngine
{
    internal enum Behavior { Succeed, ThrowSplit, Cancel }

    private readonly IReadOnlyDictionary<string, Behavior>? _behaviorByName;
    private readonly Behavior _default;
    private readonly IReadOnlyList<string>? _successWarnings;
    private readonly CancellationTokenSource? _cancelSource;
    private readonly string? _throwStdErr;

    public List<SplitRequest> Received { get; } = new();

    public int CallCount => Received.Count;

    public FakeSplitEngine(
        Dictionary<string, Behavior>? behaviorByName = null,
        Behavior @default = Behavior.Succeed,
        IReadOnlyList<string>? successWarnings = null,
        CancellationTokenSource? cancelSource = null,
        string? throwStdErr = null)
    {
        _behaviorByName = behaviorByName;
        _default = @default;
        _successWarnings = successWarnings;
        _cancelSource = cancelSource;
        _throwStdErr = throwStdErr;
    }

    public async Task<SplitResult> SplitAsync(
        SplitRequest req,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        IProgress<OperationStatus>? status = null,
        IProgress<PartProgress>? partProgress = null)
    {
        Received.Add(req);

        // Every request the batch runner builds must be a clean stream-copy request (subset path).
        req.SelectedSegmentIndices.Should().NotBeNull();

        var key = Path.GetFileName(req.InputPath);
        var behavior = _behaviorByName is not null && _behaviorByName.TryGetValue(key, out var b) ? b : _default;

        progress?.Report(0.5);

        switch (behavior)
        {
            case Behavior.Cancel:
                _cancelSource?.Cancel();
                ct.ThrowIfCancellationRequested();
                throw new OperationCanceledException(ct);

            case Behavior.ThrowSplit:
                throw new SplitException(
                    $"The trim of '{key}' failed (ffmpeg exit -22).",
                    logFilePath: null,
                    fullStdErr: _throwStdErr ?? $"stderr tail for {key}");

            default:
                var outputPath = Path.Combine(req.OutputDir, req.NamingPattern);
                Directory.CreateDirectory(req.OutputDir);
                await File.WriteAllTextAsync(outputPath, "trimmed", ct).ConfigureAwait(false);
                progress?.Report(1.0);
                return new SplitResult(
                    new[] { new SplitSegment(outputPath, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero) },
                    _successWarnings ?? Array.Empty<string>());
        }
    }
}
