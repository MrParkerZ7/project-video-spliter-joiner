using System.Globalization;
using System.Text.Json;

namespace VideoSplitJoiner.Bench;

/// <summary>The filters of <c>bench show</c>: session (an id or <c>last</c>), fixture, op substring, section (<c>--scenario</c>), area.</summary>
internal sealed record ShowFilter(string? Session, string? Fixture, string? Op, string? Scenario, string? Area);

/// <summary>
/// <c>bench core</c>, <c>bench alt</c> and <c>bench show</c>, and the input resolution they share: every input
/// (a matrix key or a path) goes through <see cref="FixtureRoot.Resolve"/> before anything is read, then must be
/// a fixture that passed its self-check (a key, or the path of a fixture listed in the root's manifest) or one of
/// the bench's own work files (a path under <c>work/</c>).
/// </summary>
internal static class Commands
{
    /// <summary>The <c>core</c> scenarios, in the order they run on each fixture.</summary>
    public static readonly string[] CoreScenarioNames = { "probe", "kf", "wf", "thumb", "load", "split", "join", "bulk", "smart" };

    private static readonly string[] Smart2Cases = { "4k104", "hevc10", "hevc44", "4k10cuda", "g10", "4k10m540" };

    /// <summary>
    /// <c>bench core &lt;fixtures…&gt; &lt;scenarios…&gt;</c>; no fixture = every valid one, no scenario = all. Each scenario
    /// runs through <see cref="BenchContext.RunSection"/>: one that throws gets a FAILED row and the rest still run.
    /// </summary>
    public static async Task<int> Core(BenchContext c, IReadOnlyList<string> tokens)
    {
        var (inputs, sections) = Split(tokens, CoreScenarioNames, Array.Empty<string>());
        var fixtures = await ResolveInputs(c, inputs);
        c.Started = true;
        bool On(string s) => sections.Count == 0 || sections.Contains(s);
        c.Results.Note($"core: fixtures {string.Join(' ', fixtures.Select(f => f.Key))}; scenarios {(sections.Count == 0 ? "all" : string.Join(' ', sections))}; runs {c.Runs}");
        foreach (var fx in fixtures)
        {
            c.Output.WriteLine($"===== {fx.Key} {fx.File} {Io.Mb(new FileInfo(fx.Path).Length)} ({(c.Recipes.TryGetValue(fx.Key, out var r) ? r : "?")})");
            if (On("probe"))
            {
                await c.RunSection("probe", fx.Key, () => CoreScenarios.Probe(c, fx, c.Runs + 1));
            }

            if (On("kf"))
            {
                await c.RunSection("kf", fx.Key, () => CoreScenarios.Keyframes(c, fx, c.Runs));
            }

            if (On("wf"))
            {
                await c.RunSection("wf", fx.Key, () => CoreScenarios.Waveform(c, fx, c.Runs));
            }

            if (On("thumb"))
            {
                await c.RunSection("thumb", fx.Key, () => CoreScenarios.Thumbnails(c, fx, c.Runs));
            }

            if (On("load"))
            {
                await c.RunSection("load", fx.Key, () => CoreScenarios.SplitLoad(c, fx, c.Runs));
            }

            // `join` needs the parts of one full split; only `split` itself times the selection and cold variants.
            IReadOnlyList<string> parts = Array.Empty<string>();
            if (On("split") || On("join"))
            {
                await c.RunSection(On("split") ? "split" : "join", fx.Key,
                    async () => parts = await CoreScenarios.SplitExport(c, fx, On("split") ? c.Runs : 1, full: On("split")));
            }

            if (On("join"))
            {
                if (parts.Count == 0)
                {
                    c.RecordFailure("join", fx.Key, ExitCodes.Unexpected, "not run: the split that makes its parts failed (see the row above)");
                }
                else
                {
                    await c.RunSection("join", fx.Key, () => CoreScenarios.Join(c, fx, parts, c.Runs));
                }
            }

            if (On("bulk"))
            {
                await c.RunSection("bulk", fx.Key, () => CoreScenarios.Bulk(c, fx));
            }

            if (On("smart"))
            {
                await c.RunSection("smart", fx.Key, () => CoreScenarios.Smart(c, fx));
            }
        }

        return Finish(c);
    }

    /// <summary>The command's exit code: Ok, or the first failed scenario's code, with a note listing the failures.</summary>
    private static int Finish(BenchContext c)
    {
        if (c.Failures.Count > 0)
        {
            c.Results.Note($"{c.Failures.Count} scenario(s) failed: {string.Join("; ", c.Failures.Select(f => $"{f.Section} on {f.Fixture} ({f.Message})"))}");
        }

        return c.ExitCode;
    }

    /// <summary><c>bench alt &lt;fixtures…&gt; &lt;sections…&gt;</c> — the ffmpeg-level alternatives (see <see cref="Alternatives"/>).</summary>
    public static async Task<int> Alt(BenchContext c, IReadOnlyList<string> tokens)
    {
        var (inputs, sections) = Split(tokens, Alternatives.Sections, Smart2Cases);
        var fixtures = await ResolveInputs(c, inputs);
        c.Started = true;
        c.Results.Note($"alt: fixtures {string.Join(' ', fixtures.Select(f => f.Key))}; sections {(sections.Count == 0 ? "all" : string.Join(' ', sections))}");

        // smart2 and join name their own fixtures; they may use any valid one, not only those given.
        var manifest = Manifest.Load(c.Root);
        Fixture? Get(string key)
        {
            var given = fixtures.FirstOrDefault(f => f.Key == key);
            if (given is not null)
            {
                return given;
            }

            var spec = FixtureMatrix.Find(key);
            var entry = spec is null ? null : manifest.Valid(c.Root, key);
            if (spec is null || entry is null)
            {
                return null;
            }

            c.Recipes[key] = entry.Recipe;
            return Fixture.From(spec, c.Root.Resolve(c.Root.Child(spec.File)), entry);
        }

        await Alternatives.Run(c, fixtures, Get, sections);
        return Finish(c);
    }

    /// <summary>Split tokens into fixture inputs and section names; anything else that is not a path is a usage error.</summary>
    private static (List<string> Inputs, List<string> Sections) Split(IReadOnlyList<string> tokens, string[] sections, string[] extraWords)
    {
        var inputs = new List<string>();
        var named = new List<string>();
        var smart2 = tokens.Contains("smart2");
        foreach (var t in tokens)
        {
            // With smart2 present, its case ids (g10 is one) name smart2 cases, not fixtures.
            if (sections.Contains(t) || (smart2 && extraWords.Contains(t)))
            {
                named.Add(t);
            }
            else if (FixtureMatrix.Find(t) is not null || LooksLikePath(t))
            {
                inputs.Add(t);
            }
            else
            {
                throw new BenchException(ExitCodes.Usage,
                    $"'{t}' is neither a fixture ({string.Join(", ", FixtureMatrix.All.Select(f => f.Key))}), a path, nor a section ({string.Join(", ", sections)}).");
            }
        }

        return (inputs, named);
    }

    private static bool LooksLikePath(string t) =>
        t.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || Path.HasExtension(t);

    /// <summary>
    /// The guard first — every input resolved (or the command refused) before any file is read — then the
    /// manifest: a key must name a fixture that passed its self-check and has not changed since; a path must be a
    /// fixture listed in the root's manifest (and unchanged) or a file under the root's <c>work/</c>, which only the
    /// bench writes. So <c>--root &lt;some folder&gt; &lt;a file in it&gt;</c> is refused: the bench opens only what it made.
    /// Every input is checked before the first one is probed.
    /// </summary>
    public static async Task<List<Fixture>> ResolveInputs(BenchContext c, IReadOnlyList<string> inputs)
    {
        var resolved = new List<(string Token, FixtureSpec? Spec, string Path)>();
        foreach (var token in inputs)
        {
            var spec = FixtureMatrix.Find(token);
            resolved.Add((token, spec, c.Root.Resolve(spec is null ? token : c.Root.Child(spec.File))));
        }

        var manifest = Manifest.Load(c.Root);
        if (resolved.Count == 0)
        {
            var valid = FixtureMatrix.All.Where(s => manifest.Valid(c.Root, s.Key) is not null).ToList();
            if (valid.Count == 0)
            {
                throw new BenchException(ExitCodes.NoFixture,
                    $"no valid fixtures under '{c.Root.Real}' — run `bench fixtures` (with the same --root) first.");
            }

            resolved.AddRange(valid.Select(s => (s.Key, (FixtureSpec?)s, c.Root.Resolve(c.Root.Child(s.File)))));
        }

        // 1. Check every input; nothing is opened yet.
        var checkedInputs = new List<(FixtureSpec? Spec, ManifestEntry? Entry, string Path, string Recipe)>();
        foreach (var (token, spec, path) in resolved)
        {
            if (spec is not null)
            {
                var entry = manifest.Valid(c.Root, spec.Key) ?? throw new BenchException(ExitCodes.NoFixture,
                    $"fixture '{spec.Key}' is not generated, or changed since its self-check, under '{c.Root.Real}' — run `bench fixtures {spec.Key}`.");
                checkedInputs.Add((spec, entry, path, entry.Recipe));
                continue;
            }

            var listed = manifest.Fixtures.Keys
                .Select(k => (Key: k, Entry: manifest.Valid(c.Root, k)))
                .FirstOrDefault(x => x.Entry is not null
                    && c.Root.TryResolve(c.Root.Child(x.Entry.File), out var p, out _)
                    && string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (listed.Entry is not null)
            {
                // The path of a listed fixture: a matrix key runs as that key, another (the selftest's clips) by its probed times.
                checkedInputs.Add((FixtureMatrix.Find(listed.Key), listed.Entry, path, listed.Entry.Recipe));
                continue;
            }

            var work = Path.TrimEndingDirectorySeparator(c.Root.WorkDir) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(work, StringComparison.OrdinalIgnoreCase))
            {
                throw new BenchException(ExitCodes.PathRefused,
                    $"refused '{token}': the bench only opens the fixtures it generated (listed in '{c.Root.ManifestFile}', unchanged since " +
                    $"their self-check) and its own work files (under '{c.Root.WorkDir}'); this file is neither.");
            }

            if (!File.Exists(path))
            {
                throw new BenchException(ExitCodes.NoFixture, $"'{token}' does not exist under the fixture root.");
            }

            checkedInputs.Add((null, null, path, "work file under the root"));
        }

        // 2. Build the fixtures (a path fixture is probed for its times).
        var fixtures = new List<Fixture>();
        foreach (var (spec, entry, path, recipe) in checkedInputs)
        {
            var fx = spec is not null ? Fixture.From(spec, path, entry!) : await Fixture.FromPath(c, path);
            c.Recipes[fx.Key] = recipe;
            fixtures.Add(fx);
        }

        return fixtures;
    }

    /// <summary>
    /// <c>bench show</c> — the prototype's show.py filter: print ledger rows filtered by session (an id, or
    /// <c>last</c>), fixture, op substring, section (<c>--scenario</c>: the core scenario or alt section the command
    /// ran) and area (<c>--area</c>: the app area the T-187 rows carry in their <c>scenario</c> field);
    /// <c>--markdown</c> prints them as a Build-log table. An unknown section, or a filter no row matches, is refused
    /// with what the ledger does hold, so an empty table is never pasted by mistake.
    /// </summary>
    public static int Show(FixtureRoot root, TextWriter o, ShowFilter f, bool markdown)
    {
        var (session, fixture, op, scenario, area) = f;
        if (!File.Exists(root.TimingsFile))
        {
            throw new BenchException(ExitCodes.NoFixture, $"no results yet at '{root.TimingsFile}'.");
        }

        var lines = new List<ResultLine>();
        foreach (var raw in File.ReadLines(root.TimingsFile))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                var l = JsonSerializer.Deserialize<ResultLine>(raw);
                if (l is not null)
                {
                    lines.Add(l);
                }
            }
            catch (JsonException)
            {
                // A line from another tool or a torn write: skip it.
            }
        }

        if (string.Equals(session, "last", StringComparison.OrdinalIgnoreCase))
        {
            session = lines.LastOrDefault()?.Session;
        }

        var sectionsInLedger = lines.Select(l => l.Section).OfType<string>().Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (scenario is not null)
        {
            var known = CoreScenarioNames.Concat(Alternatives.Sections).Concat(new[] { "fixtures", "selftest" });
            if (!known.Contains(scenario) && !sectionsInLedger.Contains(scenario))
            {
                throw new BenchException(ExitCodes.Usage,
                    $"--scenario '{scenario}' is not a scenario or section. It names what a command ran: core {string.Join(' ', CoreScenarioNames)}; " +
                    $"alt {string.Join(' ', Alternatives.Sections)}; fixtures; selftest. (The app area of the T-187 rows — shared, split, join, bulk, alt-* — is --area.)");
            }
        }

        var rows = lines.Where(l =>
                (session is null || l.Session == session)
                && (fixture is null || l.Fixture == fixture)
                && (op is null || l.Op.Contains(op, StringComparison.OrdinalIgnoreCase))
                && (scenario is null || l.Section == scenario)
                && (area is null || l.Scenario == area))
            .ToList();

        if (rows.Count == 0)
        {
            var inScope = lines.Where(l => session is null || l.Session == session).ToList();
            var held = inScope.Select(l => l.Section).OfType<string>().Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            var unsectioned = inScope.Count(l => l.Section is null);
            throw new BenchException(ExitCodes.NoFixture,
                $"no ledger row matches{Describe(f with { Session = session })}. {(session is null ? "The ledger" : $"Session {session}")} holds " +
                $"{inScope.Count} row(s); sections: {(held.Count == 0 ? "none" : string.Join(' ', held))}" +
                (unsectioned > 0 ? $"; {unsectioned} row(s) from before the section field (filter those with --area)." : "."));
        }

        if (markdown)
        {
            Results.WriteMarkdown(o, rows);
            return ExitCodes.Ok;
        }

        foreach (var d in rows)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{d.Session,-22} {d.Section ?? "-",-8} {d.Scenario,-10} {(d.Op.Length > 60 ? d.Op[..60] : d.Op),-60} {d.Fixture,-6} {d.Mode,-5} #{d.Run} {d.WallSeconds,8:0.000}s cpu {d.CpuSeconds,7:0.00}s read {d.ReadBytes / 1048576.0,8:0.0}MB load {d.OthersCores,4:0.0} {d.Note}"));
        }

        return ExitCodes.Ok;
    }

    private static string Describe(ShowFilter f)
    {
        var parts = new List<string>();
        void Add(string name, string? v)
        {
            if (v is not null)
            {
                parts.Add($"{name} {v}");
            }
        }

        Add("session", f.Session);
        Add("fixture", f.Fixture);
        Add("op", f.Op);
        Add("scenario", f.Scenario);
        Add("area", f.Area);
        return parts.Count == 0 ? string.Empty : " " + string.Join(", ", parts);
    }
}
