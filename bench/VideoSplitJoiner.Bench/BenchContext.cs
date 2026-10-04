using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using VideoSplitJoiner.Core.Errors;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.Bench;

/// <summary>Where the app's ffmpeg shared build is, and the hint when it is not there.</summary>
internal static class FfmpegLocation
{
    /// <summary>The fetch hint every "ffmpeg missing" refusal carries.</summary>
    public const string FetchHint = "Run packaging/fetch-ffmpeg-shared.ps1 to fetch the app's ffmpeg shared build into ffmpeg-shared/.";

    /// <summary>
    /// The folder holding ffmpeg.exe and ffprobe.exe: <paramref name="overrideDir"/> when given (selftest only),
    /// otherwise <c>ffmpeg-shared/</c> found walking up from the bench binary — the build the app bundles and the
    /// tests use. Throws <see cref="BenchException"/> (<see cref="ExitCodes.FfmpegMissing"/>) with the fetch hint.
    /// </summary>
    public static string Find(string? overrideDir)
    {
        if (overrideDir is not null)
        {
            return HasBoth(overrideDir)
                ? overrideDir
                : throw new BenchException(ExitCodes.FfmpegMissing, $"ffmpeg.exe/ffprobe.exe not found in '{overrideDir}'. {FetchHint}");
        }

        var looked = new List<string>();
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ffmpeg-shared");
            looked.Add(candidate);
            if (HasBoth(candidate))
            {
                return candidate;
            }
        }

        throw new BenchException(
            ExitCodes.FfmpegMissing,
            $"ffmpeg-shared/ with ffmpeg.exe and ffprobe.exe was not found (looked up from '{AppContext.BaseDirectory}'). {FetchHint}");
    }

    private static bool HasBoth(string dir) =>
        File.Exists(Path.Combine(dir, "ffmpeg.exe")) && File.Exists(Path.Combine(dir, "ffprobe.exe"));
}

/// <summary>
/// The code a measurement ran: the repo's checked-out commit (read from .git, no process started), the identity of
/// the Core build the bench loaded, and a hint whether Core's sources were edited after that commit.
/// </summary>
internal static class RepoInfo
{
    private static readonly Lazy<string> Sha = new(ReadSha);

    private static readonly Lazy<string> CoreMvid = new(() => typeof(MediaProbe).Assembly.ManifestModule.ModuleVersionId.ToString("N"));

    private static readonly Lazy<bool?> CoreEdited = new(ReadCoreEdited);

    /// <summary>The checked-out commit's sha, or <c>unknown</c>.</summary>
    public static string GitSha => Sha.Value;

    /// <summary>
    /// The loaded VideoSplitJoiner.Core build: its module version id, which the (deterministic) compiler derives
    /// from the compiled code, so it changes whenever Core's code changes and stays the same for the same code. It
    /// tells an uncommitted "after" build from the "before" build that carries the same git sha.
    /// </summary>
    public static string CoreBuild => CoreMvid.Value;

    /// <summary>
    /// A hint, not proof: true when a source file under <c>src/Core</c> (or a root <c>Directory.Build.*</c>) was
    /// written after HEAD last moved (the HEAD reflog's time stamp), i.e. the build likely holds uncommitted Core
    /// changes; false when none was; null when it cannot be told. Reads time stamps only.
    /// </summary>
    public static bool? CoreDirtyHint => CoreEdited.Value;

    /// <summary>The folder holding VideoSplitJoiner.sln above the bench binary, or null.</summary>
    public static string? Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VideoSplitJoiner.sln")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <summary>The repo's git folder (following a worktree's <c>.git</c> pointer file).</summary>
    private static string GitDir(string root)
    {
        var gitDir = Path.Combine(root, ".git");
        if (File.Exists(gitDir))
        {
            var pointer = File.ReadAllText(gitDir).Trim();
            if (pointer.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                gitDir = Path.GetFullPath(pointer["gitdir:".Length..].Trim(), root);
            }
        }

        return gitDir;
    }

    private static bool? ReadCoreEdited()
    {
        try
        {
            var root = Root();
            var core = root is null ? null : Path.Combine(root, "src", "Core");
            if (root is null || !Directory.Exists(core))
            {
                return null;
            }

            // When HEAD last moved: git appends to the HEAD reflog on every commit, checkout, reset and merge, after the
            // files it writes, so a source file newer than it was written by something else.
            var gitDir = GitDir(root);
            var reflog = Path.Combine(gitDir, "logs", "HEAD");
            var headFile = Path.Combine(gitDir, "HEAD");
            var moved = File.Exists(reflog) ? File.GetLastWriteTimeUtc(reflog)
                : File.Exists(headFile) ? File.GetLastWriteTimeUtc(headFile)
                : (DateTime?)null;
            if (moved is null)
            {
                return null;
            }

            var limit = moved.Value.AddSeconds(2);
            var sources = new List<string>();
            CollectSources(core, sources);
            sources.AddRange(new[] { "Directory.Build.props", "Directory.Build.targets" }
                .Select(n => Path.Combine(root, n)).Where(File.Exists));
            return sources.Any(f => File.GetLastWriteTimeUtc(f) > limit);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Core's source files: everything except build output (<c>bin/</c>, <c>obj/</c>).</summary>
    private static void CollectSources(string dir, List<string> into)
    {
        into.AddRange(Directory.EnumerateFiles(dir).Where(f =>
            Path.GetExtension(f).ToLowerInvariant() is ".cs" or ".csproj" or ".props" or ".targets" or ".resx" or ".json"));
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (!name.Equals("bin", StringComparison.OrdinalIgnoreCase) && !name.Equals("obj", StringComparison.OrdinalIgnoreCase))
            {
                CollectSources(sub, into);
            }
        }
    }

    private static string ReadSha()
    {
        try
        {
            var root = Root();
            if (root is null)
            {
                return "unknown";
            }

            var gitDir = GitDir(root);
            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal))
            {
                return head;
            }

            var refName = head["ref:".Length..].Trim();
            var loose = Path.Combine(gitDir, refName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(loose))
            {
                return File.ReadAllText(loose).Trim();
            }

            var packed = Path.Combine(gitDir, "packed-refs");
            if (File.Exists(packed))
            {
                foreach (var line in File.ReadLines(packed))
                {
                    if (line.EndsWith(" " + refName, StringComparison.Ordinal))
                    {
                        return line[..line.IndexOf(' ')];
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return "unknown";
    }
}

/// <summary>
/// One command's world: the fixture root it may read, the app's own runners over the ffmpeg shared build, an
/// error-log writer that writes under the root, and the results ledger. Ported from the prototype's
/// <c>P</c>/<c>Svc</c> statics, which held the scratchpad's absolute paths.
/// </summary>
internal sealed class BenchContext
{
    public BenchContext(FixtureRoot root, string ffmpegDir, int runs, TextWriter output)
    {
        Root = root;
        FfmpegDir = ffmpegDir;
        Runs = Math.Max(1, runs);
        Output = output;
        Locator = new FfmpegBinaryLocator(Path.Combine(ffmpegDir, "ffmpeg.exe"), Path.Combine(ffmpegDir, "ffprobe.exe"));
        Ffmpeg = new FfmpegRunner(Locator);
        Ffprobe = new FfprobeRunner(Locator);
        Log = new ErrorLogWriter(root.ErrorLogDir);
        Results = new Results(root, output, this);
    }

    public FixtureRoot Root { get; }

    public string FfmpegDir { get; }

    /// <summary>Repeats per measurement (the prototype's <c>Runs</c>, default 3).</summary>
    public int Runs { get; }

    public TextWriter Output { get; }

    public FfmpegBinaryLocator Locator { get; }

    public FfmpegRunner Ffmpeg { get; }

    public FfprobeRunner Ffprobe { get; }

    public ErrorLogWriter Log { get; }

    public Results Results { get; }

    /// <summary>Fixture key → the recipe that made it, stamped on every results line.</summary>
    public Dictionary<string, string> Recipes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The scenario or section running now, as the command line names it (<c>probe</c> … <c>smart</c> for core, the
    /// alt sections, <c>fixtures</c>, <c>selftest</c>): stamped on every results line as <c>section</c>, which is
    /// what <c>bench show --scenario</c> filters on.
    /// </summary>
    public string CurrentSection { get; set; } = string.Empty;

    /// <summary>True once the command got past its input checks (only then does it clean its scratch caches).</summary>
    public bool Started { get; set; }

    /// <summary>The scenarios or sections that failed in this command, in order, with the exit code each maps to.</summary>
    public List<(string Section, string Fixture, int Code, string Message)> Failures { get; } = new();

    /// <summary>Ok when nothing failed, otherwise the first failure's exit code.</summary>
    public int ExitCode => Failures.Count == 0 ? ExitCodes.Ok : Failures[0].Code;

    public string Work => Root.WorkDir;

    /// <summary>
    /// Run one scenario or section. A failure (an exception from Core, ffprobe, the disk, or a refusal inside the
    /// scenario) does not end the run: it is recorded as a <c>FAILED</c> row naming the exception, and the next
    /// scenario runs. The command then exits with the first failure's code (<see cref="ExitCodes.Unexpected"/> for
    /// an exception that is not a <see cref="BenchException"/>).
    /// </summary>
    public async Task RunSection(string section, string fixture, Func<Task> work)
    {
        CurrentSection = section;
        try
        {
            if (Cli.InjectedFault == "section:" + section)
            {
                throw new InvalidOperationException("selftest: an injected failure in " + section);
            }

            await work().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            var code = e switch
            {
                BenchException b => b.Code,
                FfmpegNotFoundException => ExitCodes.FfmpegMissing,
                _ => ExitCodes.Unexpected,
            };
            RecordFailure(section, fixture, code, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Record a scenario that failed or could not run, as a FAILED row (not timed) and in <see cref="Failures"/>.</summary>
    public void RecordFailure(string section, string fixture, int code, string message)
    {
        CurrentSection = section;
        Failures.Add((section, fixture, code, message));
        Results.Add("error", $"FAILED: {section}", fixture, "-", 1, 0, Usage.None, $"exit {code}: {message}");
    }

    public MediaProbe NewProbe() => new(Ffprobe);

    public string NewTempDir(string kind)
    {
        var d = Path.Combine(Work, "cache", kind + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Run ffmpeg through the app's own runner; returns (seconds, result).</summary>
    public async Task<(double S, FfmpegResult R)> RunFfmpeg(params string[] args)
    {
        var a = FfmpegArgs.ForFfmpeg().Raw(args);
        var sw = Stopwatch.StartNew();
        var r = await Ffmpeg.RunAsync(a).ConfigureAwait(false);
        return (sw.Elapsed.TotalSeconds, r);
    }

    /// <summary>Run ffprobe through the app's own runner; returns (seconds, stdout).</summary>
    public async Task<(double S, string Out)> RunFfprobe(params string[] args)
    {
        var a = FfmpegArgs.ForFfprobe().Raw(args);
        var sw = Stopwatch.StartNew();
        var r = await Ffprobe.RunJsonAsync(a).ConfigureAwait(false);
        return (sw.Elapsed.TotalSeconds, r);
    }

    /// <summary>PSNR (average, dB) between two images/videos via ffmpeg's psnr filter. inf → 99.</summary>
    public async Task<double> Psnr(string a, string b, string? scaleTo = null)
    {
        var filter = scaleTo is null
            ? "[0:v][1:v]psnr"
            : $"[0:v]scale={scaleTo}:flags=bicubic[a];[1:v]scale={scaleTo}:flags=bicubic[b];[a][b]psnr";
        var (_, r) = await RunFfmpeg("-i", a, "-i", b, "-lavfi", filter, "-f", "null", "-").ConfigureAwait(false);
        return ParsePsnr(r);
    }

    /// <summary>The average PSNR from an ffmpeg psnr-filter run's stderr; inf → 99, none → NaN.</summary>
    public static double ParsePsnr(FfmpegResult r)
    {
        foreach (var line in r.StdErrTail.Reverse())
        {
            var m = Regex.Match(line, @"average:(?<v>inf|[0-9.]+)");
            if (m.Success)
            {
                return m.Groups["v"].Value == "inf" ? 99.0 : double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
            }
        }

        return double.NaN;
    }
}

/// <summary>A synchronous <see cref="IProgress{T}"/> (the BCL one posts to a sync context).</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _h;

    public SyncProgress(Action<T> h) => _h = h;

    public void Report(T value) => _h(value);
}
