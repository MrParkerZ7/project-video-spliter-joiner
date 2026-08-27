using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using VideoSplitJoiner.Core.Bulk;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Split;
using VideoSplitJoiner.Core.Io;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-121 (epic G-041) — <see cref="OutputMode"/> is a NEW orthogonal axis on <see cref="BulkTrimOptions"/>,
/// deliberately not a fourth <see cref="CollisionPolicy"/> value: collision policy answers "what if the
/// destination is taken?", output mode answers "which destination?". The load-bearing property is that the
/// source-safety guard is untouched for the default <see cref="OutputMode.NewFile"/> and bypassed ONLY for
/// the explicitly opt-in <see cref="OutputMode.ReplaceOriginal"/>.
/// </summary>
public sealed class OutputModeTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vsj-outmode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Records the effective output path + overwrite flag the engine resolved for each row.</summary>
    private sealed class CapturingRequestBuilder : IBulkTrimRequestBuilder
    {
        public string? Effective { get; private set; }

        public bool Overwrite { get; private set; }

        public int CallCount { get; private set; }

        public Task<SplitRequest> BuildAsync(
            BulkTrimItem item, string effectiveOutputPath, bool overwrite, CancellationToken ct)
        {
            CallCount++;
            Effective = effectiveOutputPath;
            Overwrite = overwrite;
            throw new OperationCanceledException(new CancellationToken(canceled: true));
        }
    }

    private static async Task<CapturingRequestBuilder> ResolveAsync(
        string input, string desired, BulkTrimOptions opts)
    {
        var builder = new CapturingRequestBuilder();
        var engine = new BulkTrimEngine(
            new ThrowingSplitEngineStub(), builder, new FakeDiskSpaceProbe(long.MaxValue));
        var item = new BulkTrimItem(input, TimeSpan.FromSeconds(1), null, desired, Tag: 0);
        await engine.RunAsync(new[] { item }, opts);
        return builder;
    }

    /// <summary>Never reached — the capturing builder throws first, which is enough to observe the resolution.</summary>
    private sealed class ThrowingSplitEngineStub : ISplitEngine
    {
        public Task<SplitResult> SplitAsync(
            SplitRequest req,
            IProgress<double>? progress = null,
            CancellationToken ct = default,
            IProgress<OperationStatus>? status = null,
            IProgress<PartProgress>? partProgress = null)
            => throw new InvalidOperationException("the split engine must not be reached in these resolution tests");
    }

    // ---- Default mode: unchanged, source-safety fully live ------------------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task NewFile_IsTheDefault_AndResolvesToTheDesiredPath()
    {
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(input, "x");
            var desired = Path.Combine(dir, "clip_trimmed.mp4");

            var b = await ResolveAsync(input, desired, new BulkTrimOptions());

            new BulkTrimOptions().Output.Should().Be(OutputMode.NewFile, "the safe mode is the default");
            b.Effective.Should().Be(Path.GetFullPath(desired));
            b.Overwrite.Should().BeFalse();
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task NewFile_StillRefusesToWriteTheSource_EvenUnderOverwritePolicy()
    {
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(input, "x");

            // Desired == the source, with the most permissive collision policy.
            var b = await ResolveAsync(input, input, new BulkTrimOptions(CollisionPolicy.Overwrite));

            b.Effective.Should().NotBe(Path.GetFullPath(input), "the source-safety guard is untouched for NewFile");
            b.Overwrite.Should().BeFalse();
        }
        finally { Cleanup(dir); }
    }

    // ---- ReplaceOriginal: the deliberate, single-branch relaxation ----------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task ReplaceOriginal_ResolvesToTheInputPath_WithOverwrite()
    {
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(input, "x");
            var desired = Path.Combine(dir, "clip_trimmed.mp4");

            var b = await ResolveAsync(input, desired, new BulkTrimOptions(Output: OutputMode.ReplaceOriginal));

            b.Effective.Should().Be(Path.GetFullPath(input), "replace-original writes over the source");
            b.Overwrite.Should().BeTrue();
        }
        finally { Cleanup(dir); }
    }

    [Trait("serves-spec", "SPEC-002")]
    [Theory]
    [InlineData(CollisionPolicy.AutoSuffix)]
    [InlineData(CollisionPolicy.Skip)]
    [InlineData(CollisionPolicy.Overwrite)]
    public async Task ReplaceOriginal_IgnoresTheCollisionPolicy_Identically(CollisionPolicy policy)
    {
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(input, "x");
            var desired = Path.Combine(dir, "clip_trimmed.mp4");
            File.WriteAllText(desired, "taken"); // would matter under NewFile; irrelevant here

            var b = await ResolveAsync(input, desired, new BulkTrimOptions(policy, OutputMode.ReplaceOriginal));

            b.Effective.Should().Be(Path.GetFullPath(input), "the destination is always the source, whatever the policy");
            b.Overwrite.Should().BeTrue();
            b.CallCount.Should().Be(1, "the row is still dispatched exactly once — resolution is not a retry loop");
        }
        finally { Cleanup(dir); }
    }

    // ---- Performance: resolution is pure path math -------------------------------------------

    [Trait("serves-spec", "SPEC-002")]
    [Fact]
    public async Task Resolution_DoesNotProbeTheDisk_ForReplaceOriginal()
    {
        var dir = NewDir();
        try
        {
            var input = Path.Combine(dir, "clip.mp4");
            File.WriteAllText(input, "x");

            var probe = new CountingDiskProbe();
            var builder = new CapturingRequestBuilder();
            var engine = new BulkTrimEngine(new ThrowingSplitEngineStub(), builder, probe);
            var item = new BulkTrimItem(input, TimeSpan.FromSeconds(1), null, Path.Combine(dir, "out.mp4"), Tag: 0);

            await engine.RunAsync(new[] { item }, new BulkTrimOptions(Output: OutputMode.ReplaceOriginal));

            builder.Effective.Should().Be(Path.GetFullPath(input));
            probe.Calls.Should().BeLessThanOrEqualTo(
                1, "path resolution is pure string math — only the batch disk pre-flight may measure, once");
        }
        finally { Cleanup(dir); }
    }

    private sealed class CountingDiskProbe : IDiskSpaceProbe
    {
        public int Calls { get; private set; }

        public long? GetAvailableFreeBytes(string driveRoot)
        {
            Calls++;
            return long.MaxValue;
        }
    }
}
