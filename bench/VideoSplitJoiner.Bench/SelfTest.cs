using System.Text.Json;

namespace VideoSplitJoiner.Bench;

/// <summary>
/// T-188: <c>bench selftest</c> — the guard, the job accounting, the fixture self-checks and the results file,
/// each checked for real. It works in <c>&lt;root&gt;/selftest/</c>, which it rebuilds each run: an inner root
/// (<c>selftest/root</c>) with its own fixtures and ledger, and a folder beside it (<c>selftest/outside</c>) that
/// the inner root must never read. The guard cases run the real command line in-process, so a refused path can
/// be checked to have started no process and read no byte (the job's deltas). The accounting check needs the
/// main root's <c>4k</c> fixture (<c>bench fixtures 4k</c>). The two failure-path cases inject their failure through
/// <see cref="Cli.InjectedFault"/>, so they do not depend on a bug in Core.
/// </summary>
internal static class SelfTest
{
    private const long TenMb = 10L << 20;

    private enum Verdict
    {
        Pass,
        Fail,
        Skip,
    }

    public static async Task<int> Run(BenchContext main)
    {
        var o = main.Output;
        main.Started = true;
        main.CurrentSection = "selftest";
        var verdicts = new List<(string Name, Verdict V)>();
        void Report(string name, Verdict v, string detail)
        {
            verdicts.Add((name, v));
            o.WriteLine($"{v.ToString().ToUpperInvariant(),-4}  {name}: {detail}");
        }

        void Check(string name, bool pass, string detail) => Report(name, pass ? Verdict.Pass : Verdict.Fail, detail);

        var mainLedgerBefore = File.Exists(main.Root.TimingsFile) ? new FileInfo(main.Root.TimingsFile).Length : 0;
        var dir = main.Root.Child("selftest");
        var innerPath = Path.Combine(dir, "root");
        var outside = Path.Combine(dir, "outside");
        var link = Path.Combine(innerPath, "link");
        var volumeLink = Path.Combine(innerPath, "vol");
        var symlink = Path.Combine(innerPath, "decoy-link.mp4");
        foreach (var l in new[] { link, volumeLink, symlink })
        {
            TryDeleteLink(l);
        }

        Io.TryDelete(dir);

        // selftest/ is itself a root (the guard control's), and selftest/root the inner one: each is claimed while empty.
        var dirRoot = new FixtureRoot(dir);
        dirRoot.Claim();
        Directory.CreateDirectory(innerPath);
        Directory.CreateDirectory(outside);
        var innerRoot = new FixtureRoot(innerPath);
        innerRoot.Claim();
        var inner = new BenchContext(innerRoot, main.FfmpegDir, 1, o) { CurrentSection = "selftest", Started = true };
        o.WriteLine($"selftest under '{dir}'");

        // ---- 1. Fixture self-checks: the 4k recipe at selftest length (4 s), with its B-frames and with -bf 0.
        var spec4k = FixtureMatrix.Find("4k")!;
        var good = spec4k with { Key = "st-4k", File = "st-4k_h264_g2s_4s.mp4", Duration = 4 };
        var bad = spec4k with { Key = "st-4k-bf0", File = "st-4k-bf0_h264_g2s_4s.mp4", Duration = 4 };
        var g = await FixtureGenerator.GenerateOne(inner, good, nvenc: false);
        Check("self-check accepts the 4k recipe (4 s, -bf 2) and records it valid",
            g is { Passed: true } && Manifest.Load(inner.Root).Valid(inner.Root, good.Key) is not null,
            g?.Summary ?? "ffmpeg failed");

        // Encoded with -bf 0, checked as the 4k recipe it claims to be.
        var b = await FixtureGenerator.GenerateOne(inner, bad, nvenc: false, encodeAs: bad with { BFrames = 0 });
        var onlyBFrames = b is not null && !b.Passed && b.Checks.Where(x => !x.Ok).All(x => x.Name == "B-frames");
        Check("self-check rejects the 4k recipe with -bf 0, on B-frames alone, and does not record it",
            onlyBFrames && Manifest.Load(inner.Root).Valid(inner.Root, bad.Key) is null && !File.Exists(inner.Root.Child(bad.File)),
            b?.Summary ?? "ffmpeg failed");

        // ---- 2. The guard. A decoy (a copy of our own clip) outside the inner root, links to it, and a stray copy inside.
        var clip = inner.Root.Child(good.File);
        var decoy = Path.Combine(outside, "decoy.mp4");
        var stray = Path.Combine(innerPath, "stray.mp4");
        if (File.Exists(clip))
        {
            File.Copy(clip, decoy, overwrite: true);
            File.Copy(clip, stray, overwrite: true);

            // The control root (selftest/) lists the decoy as one of its fixtures, so the control is accepted for the
            // right reason: the path leads to a listed fixture inside that root.
            ListAsFixture(dirRoot, "st-decoy", Path.Combine("outside", "decoy.mp4"));
        }

        string? junctionError = null;
        try
        {
            Junction.Create(link, outside);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            junctionError = e.Message;
        }

        string? volumeError = null;
        var volumeSubstitute = Junction.VolumeGuidSubstitute(outside);
        if (volumeSubstitute is null)
        {
            volumeError = "the drive has no volume GUID (a SUBST or network drive)";
        }
        else
        {
            try
            {
                Junction.CreateRaw(volumeLink, volumeSubstitute, Path.GetFullPath(outside));
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                volumeError = e.Message;
            }
        }

        string? symlinkError = null;
        try
        {
            File.CreateSymbolicLink(symlink, decoy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            symlinkError = e.Message;
        }

        // Warm-up, not measured: one refusal through each refusing code path, so first-use runtime work is not counted.
        await Guarded(main, innerPath, Path.Combine(outside, "warm-up.mp4"));
        await Guarded(main, innerPath, stray);

        async Task Refused(string name, string path)
        {
            var (code, u, err) = await Guarded(main, innerPath, path);
            Check(name, code == ExitCodes.PathRefused && u.Processes == 0 && u.ReadBytes == 0,
                $"exit {code} (refused = {ExitCodes.PathRefused}), processes started {u.Processes}, bytes read {u.ReadBytes} — {Trim(err)}");
        }

        var (acode, au, aerr) = await Guarded(main, innerPath, clip);
        Check("guard: a path inside the root is accepted", acode == ExitCodes.Ok && au.Processes >= 1 && au.ReadBytes > 0,
            $"exit {acode}, processes started {au.Processes}, read {Results.Mb(au.ReadBytes)}{(acode == 0 ? string.Empty : " — " + Trim(aerr))}");
        await Refused("guard: a path outside the root is refused", decoy);
        await Refused("guard: a '..' path that escapes the root is refused", Path.Combine(innerPath, "..", "outside", "decoy.mp4"));
        if (junctionError is null)
        {
            await Refused("guard: a junction inside the root that points outside is refused", Path.Combine(link, "decoy.mp4"));

            // Control: the same path under a root that holds the junction's target (and lists it as a fixture) is
            // accepted, so the refusal above is the guard's, not a broken link's.
            var (ccode, cu, cerr) = await Guarded(main, dir, Path.Combine(link, "decoy.mp4"));
            Check("guard control: that junction path is accepted under a root that holds its target and lists it as a fixture",
                ccode == ExitCodes.Ok && cu.Processes >= 1 && cu.ReadBytes > 0,
                $"exit {ccode}, processes started {cu.Processes}, read {Results.Mb(cu.ReadBytes)}{(ccode == 0 ? string.Empty : " — " + Trim(cerr))}");
        }
        else
        {
            Check("guard: a junction inside the root that points outside is refused", false, "could not create the junction: " + junctionError);
        }

        if (volumeError is null)
        {
            await Refused(@"guard: a junction to a volume-GUID path (\??\Volume{…}\) that leads outside is refused", Path.Combine(volumeLink, "decoy.mp4"));
        }
        else
        {
            Report(@"guard: a junction to a volume-GUID path (\??\Volume{…}\) that leads outside is refused", Verdict.Skip,
                "could not create the volume-GUID junction: " + volumeError);
        }

        if (symlinkError is null)
        {
            await Refused("guard: a symbolic link inside the root that points outside is refused", symlink);
        }
        else
        {
            Report("guard: a symbolic link inside the root that points outside is refused", Verdict.Skip,
                "this account cannot create symbolic links (" + symlinkError + "); the junction case covers a link out of the root");
        }

        // A media file under the root that the bench did not generate: refused before anything but the manifest is read.
        {
            var manifestLength = new FileInfo(inner.Root.ManifestFile).Length;
            var (scode, su, serr) = await Guarded(main, innerPath, stray);
            Check("guard: a media file under the root that is not in its fixtures.json (nor under work/) is refused",
                scode == ExitCodes.PathRefused && su.Processes == 0 && su.ReadBytes <= manifestLength,
                $"exit {scode}, processes started {su.Processes}, bytes read {su.ReadBytes} (fixtures.json is {manifestLength}) — {Trim(serr)}");
        }

        // ---- 3. Job accounting is real: the packet scan reads the whole 4k file, a metadata probe almost nothing.
        var e4k = Manifest.Load(main.Root).Valid(main.Root, "4k");
        if (e4k is null)
        {
            Check("job accounting: ffprobe -show_packets on 4k reads >= 90% of the file", false,
                $"the 4k fixture is not valid under '{main.Root.Real}' — run `bench fixtures 4k` first");
            Check("job accounting: a metadata probe of 4k reads under 10 MB", false, "needs the 4k fixture");
        }
        else
        {
            inner.Recipes["4k"] = e4k.Recipe;
            var p4k = main.Root.Resolve(main.Root.Child(e4k.File));
            var size = new FileInfo(p4k).Length;
            IReadOnlyList<TimeSpan>? kf = null;
            var (s1, u1) = await Meter.Run(async () => kf = await main.NewProbe().GetKeyframesAsync(p4k));
            inner.Results.Add("selftest", "MediaProbe.GetKeyframesAsync (ffprobe -show_packets) on 4k", "4k", "warm", 1, s1, u1, $"{kf!.Count} keyframes");
            Check("job accounting: ffprobe -show_packets on 4k reads >= 90% of the file",
                u1.ReadBytes >= 0.9 * size && u1.Processes >= 1,
                $"read {Results.Mb(u1.ReadBytes)} of {Results.Mb(size)} ({100.0 * u1.ReadBytes / size:0.0}%), {u1.Processes} process(es), {s1:0.000} s");
            var (s2, u2) = await Meter.Run(() => main.NewProbe().ProbeAsync(p4k));
            inner.Results.Add("selftest", "MediaProbe.ProbeAsync on 4k", "4k", "warm", 1, s2, u2);
            Check("job accounting: a metadata probe of 4k reads under 10 MB",
                u2.ReadBytes < TenMb && u2.Processes >= 1,
                $"read {Results.Mb(u2.ReadBytes)}, {u2.Processes} process(es), {s2:0.000} s");
        }

        // ---- 4. An empty fixture root is refused; a folder that is not a bench root is not used as one.
        var emptyRoot = Path.Combine(dir, "empty-root");
        Directory.CreateDirectory(emptyRoot);
        var (e1, _, _, m1) = await RunCli(main, new[] { "core", "--root", emptyRoot });
        Check("bench core on an empty fixture root refuses with a message", e1 != ExitCodes.Ok && m1.Contains("no valid fixtures", StringComparison.Ordinal),
            $"exit {e1} — {Trim(m1)}");
        var (e2, _, _, m2) = await RunCli(main, new[] { "core", "--root", emptyRoot, "4k", "kf" });
        Check("bench core 4k on an empty fixture root refuses with a message", e2 != ExitCodes.Ok && m2.Contains("bench fixtures 4k", StringComparison.Ordinal),
            $"exit {e2} — {Trim(m2)}");

        var foreign = Path.Combine(dir, "foreign");
        var keep = new[] { Path.Combine(foreign, "work", "cache", "keep.txt"), Path.Combine(foreign, "selftest", "keep.txt") };
        foreach (var k in keep)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(k)!);
            await File.WriteAllTextAsync(k, "not the bench's");
        }

        var (ef, uf, _, mf) = await RunCli(main, new[] { "core", "--root", foreign });
        var intact = keep.All(File.Exists) && !File.Exists(Path.Combine(foreign, FixtureRoot.MarkerName));
        Check($"a non-empty folder without the {FixtureRoot.MarkerName} marker is refused as --root, and nothing in it is touched",
            ef == ExitCodes.PathRefused && mf.Contains(FixtureRoot.MarkerName, StringComparison.Ordinal) && intact && uf.Processes == 0,
            $"exit {ef}, its work\\cache and selftest files {(intact ? "intact" : "TOUCHED")} — {Trim(mf)}");

        // ---- 5. ffmpeg missing (the location pointed at an empty folder): the fetch hint, non-zero, nothing valid.
        var noFfmpeg = Path.Combine(dir, "no-ffmpeg");
        var noFfmpegRoot = Path.Combine(dir, "no-ffmpeg-root");
        Directory.CreateDirectory(noFfmpeg);
        var (e3, u3, _, m3) = await RunCli(main, new[] { "fixtures", "--root", noFfmpegRoot }, ffmpegOverride: noFfmpeg);
        var validAfter = Manifest.Load(new FixtureRoot(noFfmpegRoot)).ValidKeys(new FixtureRoot(noFfmpegRoot)).Count();
        Check("ffmpeg missing: non-zero exit with the fetch hint, no fixture recorded valid",
            e3 != ExitCodes.Ok && m3.Contains("packaging/fetch-ffmpeg-shared.ps1", StringComparison.Ordinal) && validAfter == 0 && u3.Processes == 0,
            $"exit {e3}, valid fixtures {validAfter}, processes started {u3.Processes} — {Trim(m3)}");

        // ---- 6. The ledger is append-only: a second run grows it and leaves every earlier byte as it was.
        var ledger = inner.Root.TimingsFile;
        var before = File.Exists(ledger) ? await File.ReadAllBytesAsync(ledger) : Array.Empty<byte>();
        var (e4, _, _, m4) = await RunCli(main, new[] { "core", "--root", innerPath, "--runs", "1", clip, "probe" });
        var after = File.Exists(ledger) ? await File.ReadAllBytesAsync(ledger) : Array.Empty<byte>();
        Check("a second run grows timings.jsonl and the earlier lines are byte-identical",
            e4 == ExitCodes.Ok && before.Length > 0 && after.Length > before.Length && after.AsSpan(0, before.Length).SequenceEqual(before),
            $"{before.Length} → {after.Length} bytes{(e4 == 0 ? string.Empty : " — " + Trim(m4))}");

        // `show --scenario` filters on the section a command ran (that run was `probe`), and refuses a section it does
        // not know or a filter no row matches, so an empty table is never pasted into a Build log.
        var (sh1, _, shOut1, shErr1) = await RunCli(main, new[] { "show", "--root", innerPath, "--session", "last", "--scenario", "probe", "--markdown" });
        var (sh2, _, _, shErr2) = await RunCli(main, new[] { "show", "--root", innerPath, "--scenario", "nope" });
        var (sh3, _, _, shErr3) = await RunCli(main, new[] { "show", "--root", innerPath, "--session", "last", "--scenario", "smart" });
        Check("show --scenario filters on the section a command ran, and refuses an unknown section or an empty match",
            sh1 == ExitCodes.Ok && shOut1.Contains("MediaProbe.ProbeAsync", StringComparison.Ordinal) && sh2 == ExitCodes.Usage && sh3 == ExitCodes.NoFixture,
            $"probe → exit {sh1}{(sh1 == 0 ? string.Empty : " " + Trim(shErr1))}; nope → exit {sh2} ({Trim(shErr2)}); smart → exit {sh3} ({Trim(shErr3)})");

        // ---- 7. Failures do not crash the bench: a scenario that throws gets a FAILED row and the next one still runs;
        //         an exception outside any scenario exits 1 with a message.
        int fe;
        string fErr;
        Cli.InjectedFault = "section:probe";
        try
        {
            (fe, _, _, fErr) = await RunCli(main, new[] { "core", "--root", innerPath, "--runs", "1", clip, "probe", "kf" });
        }
        finally
        {
            Cli.InjectedFault = null;
        }

        var lastRows = LastSession(ledger);
        var failedRow = lastRows.Any(r => r.Section == "probe" && r.Op == "FAILED: probe" && (r.Note ?? string.Empty).Contains("injected", StringComparison.Ordinal));
        var kfRows = lastRows.Count(r => r.Section == "kf" && !r.Op.StartsWith("FAILED", StringComparison.Ordinal));
        Check("a scenario that throws gets a FAILED row, the next scenario still runs, and the run exits 1",
            fe == ExitCodes.Unexpected && failedRow && kfRows > 0,
            $"exit {fe}, FAILED row for probe {(failedRow ? "written" : "MISSING")}, {kfRows} kf row(s) after it{(fe == ExitCodes.Unexpected ? string.Empty : " — " + Trim(fErr))}");

        int ue;
        string uErr;
        Cli.InjectedFault = "command";
        try
        {
            (ue, _, _, uErr) = await RunCli(main, new[] { "core", "--root", innerPath, "--runs", "1", clip, "probe" });
        }
        finally
        {
            Cli.InjectedFault = null;
        }

        Check("an unexpected error exits 1 with a message instead of crashing",
            ue == ExitCodes.Unexpected && uErr.Contains("unexpected error", StringComparison.Ordinal),
            $"exit {ue} — {Trim(uErr)}");

        // ---- 8. --root keeps a root's fixtures and results together: none of the above touched the main ledger.
        var mainLedgerAfter = File.Exists(main.Root.TimingsFile) ? new FileInfo(main.Root.TimingsFile).Length : 0;
        Check("--root keeps fixtures and results together (the selftest's runs left the main root's ledger alone)",
            mainLedgerAfter == mainLedgerBefore && File.Exists(Path.Combine(innerPath, "fixtures.json")) && File.Exists(ledger),
            $"main ledger {mainLedgerBefore} → {mainLedgerAfter} bytes; inner root holds fixtures.json and results/timings.jsonl");

        var failed = verdicts.Count(v => v.V == Verdict.Fail);
        var skipped = verdicts.Count(v => v.V == Verdict.Skip);
        o.WriteLine();
        o.WriteLine($"selftest: {verdicts.Count - failed - skipped} passed, {failed} failed, {skipped} skipped");
        return failed == 0 ? ExitCodes.Ok : ExitCodes.CheckFailed;
    }

    /// <summary>Run <c>bench core --root &lt;root&gt; &lt;path&gt; probe</c> in-process and measure the job around it.</summary>
    private static async Task<(int Code, Usage Usage, string Err)> Guarded(BenchContext main, string root, string path)
    {
        var (code, usage, _, err) = await RunCli(main, new[] { "core", "--root", root, "--runs", "1", path, "probe" });
        return (code, usage, err);
    }

    /// <summary>The real command line, in-process, with its output captured and the job measured around it.</summary>
    private static async Task<(int Code, Usage Usage, string Out, string Err)> RunCli(BenchContext main, string[] args, string? ffmpegOverride = null)
    {
        var outW = new StringWriter();
        var errW = new StringWriter();
        var mark = JobAccounting.Mark();
        var code = await Cli.Run(args, ffmpegOverride ?? main.FfmpegDir, outW, errW);
        var u = JobAccounting.Since(mark);
        return (code, u, outW.ToString(), errW.ToString());
    }

    /// <summary>Record <paramref name="relativeFile"/> (an existing file under <paramref name="root"/>) in that root's manifest.</summary>
    private static void ListAsFixture(FixtureRoot root, string key, string relativeFile)
    {
        var fi = new FileInfo(root.Child(relativeFile));
        var manifest = Manifest.Load(root);
        manifest.Fixtures[key] = new ManifestEntry
        {
            File = relativeFile,
            Recipe = "selftest decoy (a copy of st-4k)",
            Length = fi.Length,
            MtimeUtcTicks = fi.LastWriteTimeUtc.Ticks,
            CheckedUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        };
        manifest.Save(root);
    }

    /// <summary>The rows of the ledger's last session.</summary>
    private static List<ResultLine> LastSession(string ledger)
    {
        var rows = new List<ResultLine>();
        if (!File.Exists(ledger))
        {
            return rows;
        }

        foreach (var raw in File.ReadLines(ledger))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(raw) && JsonSerializer.Deserialize<ResultLine>(raw) is { } l)
                {
                    rows.Add(l);
                }
            }
            catch (JsonException)
            {
            }
        }

        var last = rows.LastOrDefault()?.Session;
        return rows.Where(r => r.Session == last).ToList();
    }

    private static string Trim(string s)
    {
        s = s.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
        return s.Length > 220 ? s[..220] + "…" : s;
    }

    private static void TryDeleteLink(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
