using System.Diagnostics;
using System.Globalization;
using System.Text;
using VideoSplitJoiner.Core.Ffmpeg;

namespace VideoSplitJoiner.Bench;

/// <summary>
/// T-188: the VideoSplitJoiner 4K performance bench. Dev-only: it times the real Core services on synthetic
/// fixtures it generated itself, through the app's own runners, and never ships (see bench/README.md).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // Numbers in notes, the console and the markdown table read the same on every machine (1.234, not 1,234).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        return await Cli.Run(args, ffmpegOverride: null, Console.Out, Console.Error);
    }
}

/// <summary>The command line. Also driven in-process by the selftest, which is why it takes its writers and the ffmpeg override.</summary>
internal static class Cli
{
    public const string Usage = """
        VideoSplitJoiner bench (T-188) — dev-only; synthetic fixtures only; never packaged.

          bench fixtures [keys…] [--nvenc] [--force]   generate + self-check the fixture matrix (missing or changed ones)
          bench core [fixtures…] [scenarios…]          time the Core services; scenarios: probe kf wf thumb load split join bulk smart
          bench alt [fixtures…] [sections…]            ffmpeg-level alternatives; sections: floor io probe kf thumb smart join wf decode
                                                       nofsi thumbframe smart2 [4k104 hevc10 hevc44 4k10cuda g10 4k10m540]
          bench selftest                               the guard, the job accounting, the fixture self-checks, the results file
          bench show [--session id|last] [--fixture k] [--op text] [--scenario s] [--area a] [--markdown]

          --root <dir>   fixture root (default %TEMP%\vsj-bench-fixtures); fixtures, work files and results all live under it.
                         A new or empty folder (it gets a .vsj-bench-root marker) or an existing bench root; nothing else.
          --runs <n>     repeats per measurement (default 3)

        Fixtures: 4k 1080 hevc g10 4k10m ntsc mkv. A fixture argument may also be a path inside the root, to a fixture
        listed in its fixtures.json or a file under its work\ folder.
        Results: <root>\results\timings.jsonl (append-only), and a markdown table at the end of each run.
        A scenario that throws gets a FAILED row, the run goes on, and the exit code is the first failure's (1 unexpected).
        """;

    /// <summary>
    /// Selftest only: <c>command</c> throws an unexpected exception inside the command, <c>section:&lt;name&gt;</c> inside that
    /// scenario or section, so the selftest can check both failure paths without depending on a bug in Core.
    /// </summary>
    internal static string? InjectedFault { get; set; }

    public static async Task<int> Run(string[] args, string? ffmpegOverride, TextWriter o, TextWriter err)
    {
        try
        {
            var opts = Options.Parse(args);
            switch (opts.Command)
            {
                case null or "help" or "--help" or "-h":
                    o.WriteLine(Usage);
                    return opts.Command is null ? ExitCodes.Usage : ExitCodes.Ok;
                case "show":
                    return Commands.Show(new FixtureRoot(opts.Root), o, new ShowFilter(opts.Session, opts.Fixture, opts.Op, opts.Scenario, opts.Area), opts.Markdown);
                case "fixtures" or "core" or "alt" or "selftest":
                    break;
                default:
                    throw new BenchException(ExitCodes.Usage, $"unknown command '{opts.Command}'.{Environment.NewLine}{Usage}");
            }

            // The root first: nothing is written or deleted under a folder that is not (or cannot become) a bench root.
            var root = new FixtureRoot(opts.Root);
            root.Claim(o);
            JobAccounting.Start();
            var ffmpegDir = FfmpegLocation.Find(ffmpegOverride);
            var c = new BenchContext(root, ffmpegDir, opts.Runs, o) { CurrentSection = opts.Command };
            var sw = Stopwatch.StartNew();
            try
            {
                if (InjectedFault == "command")
                {
                    throw new InvalidOperationException("selftest: an injected unexpected failure");
                }

                return opts.Command switch
                {
                    "fixtures" => await FixtureGenerator.Run(c, opts.Positional, opts.Nvenc, opts.Force),
                    "core" => await Commands.Core(c, opts.Positional),
                    "alt" => await Commands.Alt(c, opts.Positional),
                    _ => await SelfTest.Run(c),
                };
            }
            finally
            {
                // The services' per-run caches (thumbnail jpgs, waveform PCM) are scratch; drop them so work/ does not grow
                // run after run. Only once the command got past its input checks: a refused command deletes nothing.
                if (c.Started)
                {
                    Io.TryDelete(Path.Combine(c.Work, "cache"));
                }

                if (opts.Command != "selftest" && c.Results.SessionLines.Count > 0)
                {
                    c.Results.Note($"done {opts.Command} {string.Join(' ', opts.Positional)} in {sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s");
                    Results.WriteMarkdown(o, c.Results.SessionLines);
                }
            }
        }
        catch (BenchException e)
        {
            err.WriteLine("bench: " + e.Message);
            return e.Code;
        }
        catch (FfmpegNotFoundException e)
        {
            err.WriteLine("bench: " + e.Message + " " + FfmpegLocation.FetchHint);
            return ExitCodes.FfmpegMissing;
        }
        catch (Exception e)
        {
            // Anything else is a bug or an environment problem; say what it was and exit 1 rather than crash with a trace.
            err.WriteLine($"bench: unexpected error: {e.GetType().FullName}: {e.Message}");
            var frame = e.StackTrace?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (frame is not null)
            {
                err.WriteLine("  " + frame);
            }

            return ExitCodes.Unexpected;
        }
    }

    /// <summary>Parsed arguments: the command, its positional tokens, and the options.</summary>
    internal sealed class Options
    {
        public string? Command { get; private set; }

        public List<string> Positional { get; } = new();

        public string Root { get; private set; } = FixtureRoot.DefaultPath;

        public int Runs { get; private set; } = 3;

        public bool Nvenc { get; private set; }

        public bool Force { get; private set; }

        public string? Session { get; private set; }

        public string? Fixture { get; private set; }

        public string? Op { get; private set; }

        public string? Scenario { get; private set; }

        public string? Area { get; private set; }

        public bool Markdown { get; private set; }

        public static Options Parse(IReadOnlyList<string> args)
        {
            var o = new Options();
            for (var i = 0; i < args.Count; i++)
            {
                var a = args[i];
                string Value() => i + 1 < args.Count ? args[++i] : throw new BenchException(ExitCodes.Usage, $"{a} needs a value.");
                switch (a)
                {
                    case "--root":
                        o.Root = Value();
                        break;
                    case "--runs":
                        o.Runs = int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
                            ? n
                            : throw new BenchException(ExitCodes.Usage, "--runs takes a positive whole number.");
                        break;
                    case "--nvenc":
                        o.Nvenc = true;
                        break;
                    case "--force":
                        o.Force = true;
                        break;
                    case "--session":
                        o.Session = Value();
                        break;
                    case "--fixture":
                        o.Fixture = Value();
                        break;
                    case "--op":
                        o.Op = Value();
                        break;
                    case "--scenario":
                        o.Scenario = Value();
                        break;
                    case "--area":
                        o.Area = Value();
                        break;
                    case "--markdown":
                        o.Markdown = true;
                        break;
                    case "--help" or "-h":
                        o.Command ??= "help";
                        break;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new BenchException(ExitCodes.Usage, $"unknown option '{a}'.");
                        }

                        if (o.Command is null)
                        {
                            o.Command = a;
                        }
                        else
                        {
                            o.Positional.Add(a);
                        }

                        break;
                }
            }

            return o;
        }
    }
}
