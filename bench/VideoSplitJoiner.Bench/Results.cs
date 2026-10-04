using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoSplitJoiner.Bench;

/// <summary>One measurement, as one JSON line of <c>results/timings.jsonl</c>.</summary>
internal sealed class ResultLine
{
    [JsonPropertyName("session")]
    public string Session { get; set; } = string.Empty;

    /// <summary>
    /// Which part of the app the measurement belongs to (the prototype's "tab": shared, split, join, bulk, fixtures,
    /// alt-*, and error for a FAILED row). Kept under this name so the T-187-era rows read the same; <c>bench show
    /// --area</c> filters on it.
    /// </summary>
    [JsonPropertyName("scenario")]
    public string Scenario { get; set; } = string.Empty;

    /// <summary>
    /// The scenario or section of the command that wrote the row, as the command line names it (core: probe kf wf
    /// thumb load split join bulk smart; alt: its sections; fixtures; selftest). <c>bench show --scenario</c> filters
    /// on it. Absent on rows written before it existed.
    /// </summary>
    [JsonPropertyName("section")]
    public string? Section { get; set; }

    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    [JsonPropertyName("fixture")]
    public string Fixture { get; set; } = string.Empty;

    /// <summary>Which recipe made the fixture (libx264/libx265 or nvenc) — Build logs quote it.</summary>
    [JsonPropertyName("recipe")]
    public string Recipe { get; set; } = string.Empty;

    /// <summary>warm (file in the page cache), cold (a fresh unbuffered copy), or "-".</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("run")]
    public int Run { get; set; }

    [JsonPropertyName("wall_s")]
    public double WallSeconds { get; set; }

    [JsonPropertyName("cpu_s")]
    public double CpuSeconds { get; set; }

    [JsonPropertyName("read_bytes")]
    public long ReadBytes { get; set; }

    [JsonPropertyName("write_bytes")]
    public long WriteBytes { get; set; }

    [JsonPropertyName("processes")]
    public int Processes { get; set; }

    /// <summary>Machine-load sample: cores busy with work outside the bench's job during the measurement.</summary>
    [JsonPropertyName("load_others_cores")]
    public double OthersCores { get; set; }

    [JsonPropertyName("cores")]
    public int Cores { get; set; }

    [JsonPropertyName("git")]
    public string Git { get; set; } = string.Empty;

    /// <summary>The loaded Core build's module version id: differs between an uncommitted "after" build and the "before" build at the same sha.</summary>
    [JsonPropertyName("core_build")]
    public string? CoreBuild { get; set; }

    /// <summary>A hint that Core's sources were edited after the <see cref="Git"/> commit (see <see cref="RepoInfo.CoreDirtyHint"/>); null when unknown.</summary>
    [JsonPropertyName("core_dirty_hint")]
    public bool? CoreDirtyHint { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>Line-specific fields (a fixture's <c>start_time</c>, its self-check results).</summary>
    [JsonExtensionData]
    public Dictionary<string, object>? Extra { get; set; }
}

/// <summary>
/// The append-only results ledger (ported from the prototype's <c>R</c>): each measurement appends one JSON
/// line to <c>&lt;root&gt;/results/timings.jsonl</c> and echoes a console line. The file is only ever
/// opened for append, so earlier lines stay byte-identical. At the end of a command
/// <see cref="WriteMarkdown"/> prints this session's rows as a table to paste into a Build log.
/// </summary>
internal sealed class Results
{
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly FixtureRoot _root;
    private readonly TextWriter _out;
    private readonly BenchContext _ctx;
    private readonly List<ResultLine> _session = new();

    public Results(FixtureRoot root, TextWriter output, BenchContext ctx)
    {
        _root = root;
        _out = output;
        _ctx = ctx;
        Session = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..4];
    }

    /// <summary>This command's session id (UTC start time + a short tag).</summary>
    public string Session { get; }

    /// <summary>The rows this session added, in order.</summary>
    public IReadOnlyList<ResultLine> SessionLines
    {
        get
        {
            lock (_gate)
            {
                return _session.ToList();
            }
        }
    }

    /// <summary>Append one measurement.</summary>
    public ResultLine Add(
        string scenario, string op, string fixture, string mode, int run, double seconds, Usage usage,
        string? note = null, Dictionary<string, object>? extra = null)
    {
        var line = new ResultLine
        {
            Session = Session,
            Scenario = scenario,
            Section = string.IsNullOrEmpty(_ctx.CurrentSection) ? null : _ctx.CurrentSection,
            Op = op,
            Fixture = fixture,
            Recipe = _ctx.Recipes.TryGetValue(fixture, out var r) ? r : "-",
            Mode = mode,
            Run = run,
            WallSeconds = Math.Round(seconds, 4),
            CpuSeconds = Math.Round(usage.CpuSeconds, 3),
            ReadBytes = usage.ReadBytes,
            WriteBytes = usage.WriteBytes,
            Processes = usage.Processes,
            OthersCores = Math.Round(usage.OthersCores, 2),
            Cores = Environment.ProcessorCount,
            Git = RepoInfo.GitSha,
            CoreBuild = RepoInfo.CoreBuild,
            CoreDirtyHint = RepoInfo.CoreDirtyHint,
            Note =string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Extra = extra,
        };

        var json = JsonSerializer.Serialize(line, LineOptions);
        lock (_gate)
        {
            Directory.CreateDirectory(_root.ResultsDir);
            using (var fs = new FileStream(_root.TimingsFile, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                fs.Write(bytes, 0, bytes.Length);
            }

            _session.Add(line);
            _out.WriteLine(
                $"{line.Section ?? scenario,-10} {Trunc(op, 60),-60} {fixture,-6} {mode,-5} #{run} {seconds,8:0.000}s " +
                $"cpu {usage.CpuSeconds,7:0.00}s read {Mb(usage.ReadBytes),9} procs {usage.Processes,2} load {usage.OthersCores,4:0.0} {line.Note}");
        }

        return line;
    }

    /// <summary>A free-text note in <c>results/notes.txt</c> (and on the console).</summary>
    public void Note(string text)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_root.ResultsDir);
            File.AppendAllText(Path.Combine(_root.ResultsDir, "notes.txt"), $"[{Session}] {text}\n");
            _out.WriteLine("NOTE " + text);
        }
    }

    /// <summary>Print <paramref name="lines"/> as a markdown table (a Build log's "before"/"after" block).</summary>
    public static void WriteMarkdown(TextWriter w, IReadOnlyList<ResultLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var first = lines[0];
        var sessions = lines.Select(l => l.Session).Distinct().ToList();
        var builds = lines.Select(Build).Distinct().ToList();
        w.WriteLine();
        w.WriteLine(
            $"{(sessions.Count == 1 ? $"Session `{first.Session}`" : $"{sessions.Count} sessions")} · " +
            $"{(builds.Count == 1 ? builds[0] : $"{builds.Count} builds: {string.Join("; ", builds)}")} · " +
            $"{first.Cores} logical cores · fixtures under the bench root (synthetic only)");
        w.WriteLine();
        w.WriteLine("| section | area | op | fixture | recipe | mode | run | wall s | CPU s | read MB | written MB | procs | load (other cores) | note |");
        w.WriteLine("|---|---|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var l in lines)
        {
            w.WriteLine(
                $"| {l.Section ?? "-"} | {l.Scenario} | {Cell(l.Op)} | {l.Fixture} | {l.Recipe} | {l.Mode} | {l.Run} | {l.WallSeconds:0.000} | {l.CpuSeconds:0.00} | " +
                $"{l.ReadBytes / 1048576.0:0.0} | {l.WriteBytes / 1048576.0:0.0} | {l.Processes} | {l.OthersCores:0.0} | {Cell(l.Note ?? string.Empty)} |");
        }
    }

    /// <summary>The code a row ran: git sha, Core build, and the uncommitted-Core hint.</summary>
    internal static string Build(ResultLine l) =>
        $"git `{Short(l.Git)}` · Core build `{(l.CoreBuild is { Length: > 0 } b ? b[..Math.Min(8, b.Length)] : "unrecorded")}`" +
        (l.CoreDirtyHint == true ? " (Core sources edited after that commit)" : string.Empty);

    public static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB";

    private static string Short(string sha) => sha.Length > 10 ? sha[..10] : sha;

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
