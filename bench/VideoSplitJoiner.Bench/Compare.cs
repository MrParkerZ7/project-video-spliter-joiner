using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VideoSplitJoiner.Bench;

/// <summary>
/// <c>bench compare</c> (T-225) - the copy stage of a split or join cannot be compared between two builds from one session of each: on a
/// disk that is being written to, the order of the sessions (write cache, SSD state) moves the same code's numbers 2-3 times (T-193 measured
/// 1.77 s, 3.60 s and 5.21 s for one build run first and third). The method that survives it is to ALTERNATE the builds - A B A B, each
/// session on the same <c>--root</c> - and compare medians, and this command is the reading side of it: it lays the sessions side by side
/// per (fixture, op, mode), groups them by the Core build that ran (<c>core_build</c>), and says when sessions of the SAME build differ by more
/// than <see cref="SpreadTolerance"/> (then the pair is not a comparison yet).
/// </summary>
internal static class Compare
{
    /// <summary>Sessions of one build whose medians differ by more than this are an order effect, not a result.</summary>
    public const double SpreadTolerance = 0.15;

    /// <summary>One row of the table: a (fixture, op, mode) with its median wall time per session.</summary>
    internal sealed record Line(
        string Fixture,
        string Op,
        string Mode,
        IReadOnlyList<double?> PerSession,
        IReadOnlyList<BuildSummary> Builds,
        double? WrittenMbPerSecond);

    /// <summary>The sessions of one Core build for one row: the median of their medians, and how far apart they were.</summary>
    internal sealed record BuildSummary(string Build, int Sessions, double Median, double Spread)
    {
        public bool Noisy => Sessions >= 2 && Spread > SpreadTolerance;
    }

    public static int Run(FixtureRoot root, TextWriter o, ShowFilter f, bool markdown)
    {
        if (!File.Exists(root.TimingsFile))
        {
            throw new BenchException(ExitCodes.NoFixture, $"no results yet at '{root.TimingsFile}'.");
        }

        var lines = ReadLedger(root.TimingsFile);
        var sessions = SelectSessions(lines, f.Session);
        var rows = lines.Where(l =>
                sessions.Contains(l.Session)
                && (f.Fixture is null || l.Fixture == f.Fixture)
                && (f.Op is null || l.Op.Contains(f.Op, StringComparison.OrdinalIgnoreCase))
                && (f.Scenario is null || l.Section == f.Scenario)
                && (f.Area is null || l.Scenario == f.Area)
                && !l.Op.StartsWith("FAILED", StringComparison.Ordinal))
            .ToList();
        if (rows.Count == 0)
        {
            throw new BenchException(ExitCodes.NoFixture, "no ledger row of those sessions matches the filter.");
        }

        var buildOf = sessions.ToDictionary(
            s => s,
            s => Short(lines.Where(l => l.Session == s).Select(l => l.CoreBuild).FirstOrDefault(b => !string.IsNullOrEmpty(b))));
        var table = Build(rows, sessions, buildOf);

        // The machine's other work, per session (the ledger's load column: logical cores kept busy outside the bench's job, median over the
        // session): a session that ran under a heavier load than its neighbours is the usual reason a row is marked !.
        var loadOf = sessions.ToDictionary(
            s => s,
            s => Median(lines.Where(l => l.Session == s).Select(l => l.OthersCores).ToList()));
        if (markdown)
        {
            WriteMarkdown(o, sessions, buildOf, loadOf, table);
        }
        else
        {
            WriteText(o, sessions, buildOf, loadOf, table);
        }

        var noisy = table.Count(t => t.Builds.Any(b => b.Noisy));
        if (table.Count == 0)
        {
            throw new BenchException(ExitCodes.NoFixture, "those sessions hold no timed row that matches the filter.");
        }
        o.WriteLine();
        o.WriteLine(noisy == 0
            ? $"{table.Count} row(s); sessions of one build agree within {SpreadTolerance * 100:0}% on every row, so a difference between builds is readable."
            : $"{noisy} of {table.Count} row(s) differ by more than {SpreadTolerance * 100:0}% between sessions of the SAME build (marked !): the order of the sessions moves the numbers, " +
              "not the code. Run one throwaway session first, then alternate the builds A B A B on a drive with headroom and compare the medians of each (bench/README.md, Comparing two builds).");
        return ExitCodes.Ok;
    }

    /// <summary>The table, pure: for each (fixture, op, mode) the median wall of each session and the per-build summary.</summary>
    internal static IReadOnlyList<Line> Build(IReadOnlyList<ResultLine> rows, IReadOnlyList<string> sessions, IReadOnlyDictionary<string, string> buildOf)
    {
        var table = new List<Line>();
        foreach (var g in rows.GroupBy(r => (r.Fixture, r.Op, r.Mode)))
        {
            var perSession = sessions.Select(s =>
            {
                var w = g.Where(r => r.Session == s).Select(r => r.WallSeconds).ToList();
                return w.Count == 0 ? (double?)null : Median(w);
            }).ToList();

            var builds = new List<BuildSummary>();
            foreach (var b in sessions.Select(s => buildOf[s]).Distinct())
            {
                var values = sessions.Select((s, i) => (s, v: perSession[i])).Where(x => buildOf[x.s] == b && x.v is not null).Select(x => x.v!.Value).ToList();
                if (values.Count > 0)
                {
                    builds.Add(new BuildSummary(b, values.Count, Median(values), values.Min() > 0 ? (values.Max() - values.Min()) / values.Min() : 0d));
                }
            }

            // Throughput of the newest session that wrote anything: written MB over the median wall (a copy stage's speed, which the wall time
            // alone hides when the amount written differs).
            double? mbps = null;
            for (var i = sessions.Count - 1; i >= 0 && mbps is null; i--)
            {
                var mine = g.Where(r => r.Session == sessions[i]).ToList();
                if (mine.Count > 0 && Median(mine.Select(r => (double)r.WriteBytes).ToList()) > 0 && perSession[i] is { } wall && wall > 0)
                {
                    mbps = Median(mine.Select(r => r.WriteBytes / 1048576.0).ToList()) / wall;
                }
            }

            // A row that is a count and not a time (the parts' video frames, "not timed") is 0 everywhere: nothing to compare.
            if (perSession.Any(v => v is > 0))
            {
                table.Add(new Line(g.Key.Fixture, g.Key.Op, g.Key.Mode, perSession, builds, mbps));
            }
        }

        return table;
    }

    /// <summary>Which sessions: a comma list of ids, or <c>last:N</c> for the newest N (at least 2); never a single one.</summary>
    internal static IReadOnlyList<string> SelectSessions(IReadOnlyList<ResultLine> lines, string? spec)
    {
        var inLedger = lines.Select(l => l.Session).Distinct().ToList();
        if (string.IsNullOrWhiteSpace(spec))
        {
            throw new BenchException(ExitCodes.Usage, "compare needs sessions: --session id,id,... or --session last:N (the newest N, at least 2).");
        }

        if (spec.StartsWith("last:", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(spec[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 2)
            {
                throw new BenchException(ExitCodes.Usage, "--session last:N takes a whole number of at least 2.");
            }

            return inLedger.Skip(Math.Max(0, inLedger.Count - n)).ToList();
        }

        var wanted = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        var unknown = wanted.Where(w => !inLedger.Contains(w)).ToList();
        if (wanted.Count < 2 || unknown.Count > 0)
        {
            throw new BenchException(
                wanted.Count < 2 ? ExitCodes.Usage : ExitCodes.NoFixture,
                wanted.Count < 2
                    ? "compare takes at least two sessions."
                    : $"no session {string.Join(", ", unknown)} in the ledger (it holds {string.Join(", ", inLedger.TakeLast(8))}{(inLedger.Count > 8 ? ", ..." : string.Empty)}).");
        }

        return inLedger.Where(wanted.Contains).ToList();
    }

    private static List<ResultLine> ReadLedger(string file)
    {
        var lines = new List<ResultLine>();
        foreach (var raw in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<ResultLine>(raw) is { } l)
                {
                    lines.Add(l);
                }
            }
            catch (JsonException)
            {
                // A line from another tool or a torn write: skip it.
            }
        }

        return lines;
    }

    internal static double Median(IReadOnlyList<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[(s.Count / 2) - 1] + s[s.Count / 2]) / 2d;
    }

    private static string Short(string? build) => string.IsNullOrEmpty(build) ? "unrecorded" : build[..Math.Min(8, build.Length)];

    private static string Cell(double? v) => v is { } x ? x.ToString("0.000", CultureInfo.InvariantCulture) : "-";

    private static string BuildsText(Line l) =>
        string.Join("  ", l.Builds.Select(b => string.Create(CultureInfo.InvariantCulture,
            $"{b.Build} x{b.Sessions} median {b.Median:0.000}s{(b.Sessions >= 2 ? $" spread {b.Spread * 100:0}%" : string.Empty)}{(b.Noisy ? " !" : string.Empty)}")));

    private static string Ratio(Line l) =>
        l.Builds.Count == 2 && l.Builds[0].Median > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{l.Builds[1].Median / l.Builds[0].Median:0.00}x")
            : string.Empty;

    private static void WriteText(
        TextWriter o, IReadOnlyList<string> sessions, IReadOnlyDictionary<string, string> buildOf, IReadOnlyDictionary<string, double> loadOf, IReadOnlyList<Line> table)
    {
        o.WriteLine("sessions (columns, in ledger order): " + string.Join("  ", sessions.Select((s, i) => string.Create(CultureInfo.InvariantCulture, $"[{i + 1}] {s} build {buildOf[s]} load {loadOf[s]:0.0}"))));
        o.WriteLine("median wall seconds per session; per build: the median of its sessions' medians and how far apart those were (! over 15%)");
        foreach (var l in table)
        {
            var op = l.Op.Length > 56 ? l.Op[..56] : l.Op;
            var mbps = l.WrittenMbPerSecond is { } m ? string.Create(CultureInfo.InvariantCulture, $"  {m:0} MB/s written") : string.Empty;
            var ratio = Ratio(l);
            o.WriteLine($"{l.Fixture,-6} {op,-56} {l.Mode,-5} | {string.Join(" ", l.PerSession.Select(v => Cell(v).PadLeft(8)))} | {BuildsText(l)}{(ratio.Length > 0 ? "  second/first " + ratio : string.Empty)}{mbps}");
        }
    }

    private static void WriteMarkdown(
        TextWriter o, IReadOnlyList<string> sessions, IReadOnlyDictionary<string, string> buildOf, IReadOnlyDictionary<string, double> loadOf, IReadOnlyList<Line> table)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| fixture | op | mode | " + string.Join(" | ", sessions.Select((s, i) => string.Create(CultureInfo.InvariantCulture, $"[{i + 1}] {s} ({buildOf[s]}, load {loadOf[s]:0.0})"))) + " | per build (median of session medians, spread; ! = over 15%) | written MB/s |");
        sb.AppendLine("|---|---|---|" + string.Concat(sessions.Select(_ => "---:|")) + "---|---:|");
        foreach (var l in table)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {l.Fixture} | {l.Op.Trim().Replace("|", "\\|", StringComparison.Ordinal)} | {l.Mode} | {string.Join(" | ", l.PerSession.Select(Cell))} | {BuildsText(l)} | {(l.WrittenMbPerSecond is { } m ? m.ToString("0", CultureInfo.InvariantCulture) : string.Empty)} |"));
        }

        o.Write(sb.ToString());
    }
}
