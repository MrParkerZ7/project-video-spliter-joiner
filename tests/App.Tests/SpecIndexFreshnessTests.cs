using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-153 — `docs/specs/_index.md` must not drift away from the specs it indexes.
///
/// <para>It had, in <b>both directions</b>: the table claimed 633 documented invariants against a real
/// 680, SPEC-011 said 102 and had 121, SPEC-007 said 72 and had 94 — while SPEC-014 said 35 and had only
/// 30. Three days, no signal.</para>
///
/// <para>That table is not decoration. Its untested-invariant list is the project's tripwire for
/// "behaviour we documented and nobody enforces", and a tripwire that is wrong in the <i>reassuring</i>
/// direction is worse than none: it had a standing warning about two SPEC-008 invariants that were by
/// then covered, and stayed silent about everything added since. This test recounts and compares, so the
/// numbers can only be wrong deliberately.</para>
/// </summary>
public sealed class SpecIndexFreshnessTests
{
    /// <summary>An invariant definition line: <c>- **I42** — …</c>.</summary>
    private static readonly Regex InvariantLine = new(@"^\s*-\s+\*\*I(\d+)\*\*", RegexOptions.Compiled);

    /// <summary>A spec row in the index table.</summary>
    private static readonly Regex IndexRow = new(
        @"^\|\s*\[(?<id>SPEC-\d+)\]\((?<file>SPEC-[^)]+)\)\s*\|[^|]*\|[^|]*\|\s*(?<count>\d+)\s*\|",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static string? SpecsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "specs");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "_index.md")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static int CountInvariants(string specFile)
        => File.ReadAllLines(specFile).Count(l => InvariantLine.IsMatch(l));

    [Fact]
    public void TheSpecsDirectoryIsFound()
    {
        // Without this the comparison below would pass vacuously — the failure mode this whole ticket
        // is about.
        SpecsDir().Should().NotBeNull("the index cannot be checked against specs that cannot be located");
    }

    [Fact]
    public void EveryIndexedSpecExists()
    {
        var dir = SpecsDir();
        if (dir is null)
        {
            return;
        }

        var missing = new List<string>();
        foreach (Match m in IndexRow.Matches(File.ReadAllText(Path.Combine(dir, "_index.md"))))
        {
            var file = Path.Combine(dir, m.Groups["file"].Value);
            if (!File.Exists(file))
            {
                missing.Add(m.Groups["id"].Value);
            }
        }

        missing.Should().BeEmpty("the index must not point at specs that are gone");
    }

    /// <summary>
    /// Every ADR on disk must appear in <c>docs/adr/README.md</c>.
    ///
    /// <para>Found the hard way: ADR-0021 was written, committed, and never indexed — and nobody noticed
    /// until ADR-0022 tried to slot in beneath it. An unindexed decision record is one nobody finds when
    /// they go looking for why something is the way it is, which is the entire reason ADRs exist.</para>
    /// </summary>
    [Fact]
    public void EveryAdrIsIndexed()
    {
        var dir = SpecsDir();
        if (dir is null)
        {
            return;
        }

        var adrDir = Path.Combine(Path.GetDirectoryName(dir)!, "adr");
        if (!Directory.Exists(adrDir))
        {
            return;
        }

        var readme = Path.Combine(adrDir, "README.md");
        File.Exists(readme).Should().BeTrue("the ADR index is what makes the records findable");

        var index = File.ReadAllText(readme);

        var unindexed = Directory.GetFiles(adrDir, "0*.md")
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .Where(f => !index.Contains($"({f})", StringComparison.OrdinalIgnoreCase))
            .ToList();

        unindexed.Should().BeEmpty(
            "an ADR nobody indexes is a decision nobody finds:" + Environment.NewLine +
            string.Join(Environment.NewLine, unindexed));
    }

    [Fact]
    public void TheIndexInvariantCountsMatchTheSpecs()
    {
        var dir = SpecsDir();
        if (dir is null)
        {
            return;
        }

        var indexText = File.ReadAllText(Path.Combine(dir, "_index.md"));
        var rows = IndexRow.Matches(indexText);

        rows.Count.Should().BeGreaterThan(0, "the index table should still be parseable");

        var drift = new List<string>();
        foreach (Match m in rows)
        {
            var file = Path.Combine(dir, m.Groups["file"].Value);
            if (!File.Exists(file))
            {
                continue; // reported by EveryIndexedSpecExists
            }

            var listed = int.Parse(m.Groups["count"].Value);
            var actual = CountInvariants(file);
            if (listed != actual)
            {
                drift.Add($"{m.Groups["id"].Value}: index says {listed}, spec has {actual} ({actual - listed:+#;-#;0})");
            }
        }

        drift.Should().BeEmpty(
            "docs/specs/_index.md must be recounted when invariants are added or removed — it drifted " +
            "47 out of step before anything noticed:" + Environment.NewLine +
            string.Join(Environment.NewLine, drift));
    }

    [Fact]
    public void EverySpecFileAppearsInTheIndex()
    {
        var dir = SpecsDir();
        if (dir is null)
        {
            return;
        }

        var indexed = IndexRow.Matches(File.ReadAllText(Path.Combine(dir, "_index.md")))
            .Select(m => m.Groups["file"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var onDisk = Directory.GetFiles(dir, "SPEC-*.md").Select(Path.GetFileName)!;

        onDisk.Except(indexed, StringComparer.OrdinalIgnoreCase).Should().BeEmpty(
            "a spec nobody indexes is a spec nobody audits");
    }
}
