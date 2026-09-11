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
    /// <summary>
    /// An invariant definition line: <c>- **I42** — …</c>, and also <c>- **I31 (view-only)** — …</c>.
    ///
    /// <para>The qualifier form was invisible to the original pattern, which required <c>**</c> to follow the
    /// digits immediately. SPEC-014 writes five invariants that way, so both this guard AND the index counted
    /// 30 against a real 35 — two wrong numbers agreeing with each other, which is exactly the failure T-166
    /// was built to catch, one layer down where it could not see. Found by a todo-docs audit, 2026-09-11.</para>
    /// </summary>
    private static readonly Regex InvariantLine = new(@"^\s*-\s+\*\*I(\d+)(?:\s[^*]*)?\*\*", RegexOptions.Compiled);

    /// <summary>A spec row in the index table.</summary>
    private static readonly Regex IndexRow = new(
        @"^\|\s*\[(?<id>SPEC-\d+)\]\((?<file>SPEC-[^)]+)\)\s*\|[^|]*\|[^|]*\|\s*(?<count>\d+)\s*\|",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The TOTAL row at the foot of the index table.</summary>
    private static readonly Regex TotalRow = new(
        @"^\|\s*\*\*TOTAL\*\*\s*\|[^|]*\|[^|]*\|\s*\*\*(?<count>\d+)\*\*\s*\|",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The prose headline above the notes: <c>Total documented invariants: **762**</c>.</summary>
    private static readonly Regex Headline = new(
        @"Total documented invariants:\s*\*\*(?<count>\d+)\*\*",
        RegexOptions.Compiled);

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

    /// <summary>
    /// The sum of the per-spec rows, recounted from the specs themselves — the ground truth the two
    /// summary figures below are checked against. Returns null when the specs cannot be located.
    /// </summary>
    private static int? SumOfSpecRows()
    {
        var dir = SpecsDir();
        if (dir is null)
        {
            return null;
        }

        var total = 0;
        foreach (Match m in IndexRow.Matches(File.ReadAllText(Path.Combine(dir, "_index.md"))))
        {
            var file = Path.Combine(dir, m.Groups["file"].Value);
            total += File.Exists(file) ? CountInvariants(file) : int.Parse(m.Groups["count"].Value);
        }

        return total;
    }

    /// <summary>
    /// T-166 — the <c>**TOTAL**</c> row must equal the rows above it.
    ///
    /// <para>The original guard recounts each per-spec row against its spec file and is precise about
    /// it. It has nothing whatever to say about the TOTAL row, because that row's first cell is
    /// <c>**TOTAL**</c> rather than a linked <c>SPEC-NNN</c> and the row regex therefore skips it. So
    /// the one figure a reader is most likely to quote was the one figure nothing checked.</para>
    ///
    /// <para>It went wrong exactly as you would expect: the headline and the table disagreed for two
    /// days, 723 against 751, with the suite green the whole time. The index's own prose asked whoever
    /// edited it to keep them in step by hand — which is the same hand-maintained-copy arrangement that
    /// let the picker filter go stale (T-158) and let _GAPS.md claim 9 deferred while listing 13.</para>
    /// </summary>
    [Fact]
    public void TheTotalRowEqualsTheSumOfTheSpecRows()
    {
        var expected = SumOfSpecRows();
        if (expected is null)
        {
            return; // reported by TheSpecsDirectoryIsFound
        }

        var indexText = File.ReadAllText(Path.Combine(SpecsDir()!, "_index.md"));
        var row = TotalRow.Match(indexText);

        row.Success.Should().BeTrue(
            "the index must still carry a **TOTAL** row for this to check — if the table was " +
            "restructured, this guard needs to follow it rather than be deleted");

        int.Parse(row.Groups["count"].Value).Should().Be(
            expected.Value,
            "the TOTAL row must equal the per-spec rows above it, which are recounted from the specs " +
            "themselves — a summary figure that disagrees with what it summarises is worse than no " +
            "summary, because people quote it");
    }

    /// <summary>
    /// T-166 — the prose headline must equal the same sum.
    ///
    /// <para>Separate from the TOTAL row on purpose: they are two independently hand-typed copies of one
    /// number, and the two-day 723-vs-751 disagreement was between exactly these two. Checking one
    /// against the other would leave both free to drift together; both are checked against the rows,
    /// which are checked against the specs on disk.</para>
    /// </summary>
    [Fact]
    public void ThePlainEnglishHeadlineEqualsTheSameSum()
    {
        var expected = SumOfSpecRows();
        if (expected is null)
        {
            return; // reported by TheSpecsDirectoryIsFound
        }

        var indexText = File.ReadAllText(Path.Combine(SpecsDir()!, "_index.md"));
        var headline = Headline.Match(indexText);

        headline.Success.Should().BeTrue(
            "the index must still state 'Total documented invariants: **N**' — it is the figure quoted " +
            "in README and ROADMAP, so it is the one most worth pinning");

        int.Parse(headline.Groups["count"].Value).Should().Be(
            expected.Value,
            "the headline is the number a human reads; it drifted 28 away from the table for two days " +
            "with the suite green, because nothing compared them");
    }
}
