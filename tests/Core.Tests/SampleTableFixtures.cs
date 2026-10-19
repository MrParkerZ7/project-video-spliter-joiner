using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// Small, synthetic MP4/MOV shapes made with the real ffmpeg for the T-194 sample-table keyframe reader: every shape a real file
/// has that changes how a keyframe's time is written down (B-frames and the edit list that hides their delay, a video that
/// starts late, an edit list that trims the start, 64-bit and fragmented layouts, several video tracks, a join's 21 ms video
/// start). The shapes matter, not the resolution: all are 320x240 and a few seconds long. Made lazily, once per test run, in a
/// temp folder removed at the end. Tests skip (visibly) when ffmpeg is absent.
/// </summary>
public sealed class SampleTableFixtures : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsj-sampletable-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<string, Lazy<string>> _files = new();

    public SampleTableFixtures()
    {
        Directory.CreateDirectory(_dir);
    }

    /// <summary>H.264 High, 2 B-frames, GOP 30 at 30 fps for 10 s, moov at the end: ctts v0 and an edit list with media_time 1024.</summary>
    public string H264Bframes => Get("h264_bframes.mp4", out_ => Encode(out_, X264, "-bf", "2"));

    /// <summary>The same remuxed so the moov sits before the media data.</summary>
    public string H264BframesFaststart => Get("h264_bframes_faststart.mp4", out_ => Encode(out_, X264, "-bf", "2", "-movflags", "+faststart"));

    /// <summary>H.264 without B-frames: no ctts, an edit list with media_time 0.</summary>
    public string H264NoBframes => Get("h264_nobframes.mp4", out_ => Encode(out_, X264, "-bf", "0"));

    /// <summary>H.264 where every frame is a keyframe: no stss at all (every sample is a sync sample).</summary>
    public string IntraOnly => Get("intra_only.mp4", out_ => Encode(out_, "-c:v", "libx264", "-g", "1", "-pix_fmt", "yuv420p"));

    public string Hvc1 => Get("hvc1.mp4", out_ => Encode(out_, X265, "-tag:v", "hvc1"));

    public string Hev1 => Get("hev1.mp4", out_ => Encode(out_, X265, "-tag:v", "hev1"));

    public string HevcMain10 => Get("hevc_main10.mp4", out_ => Encode(out_, X265Main10, "-tag:v", "hvc1"));

    /// <summary>30000/1001 fps: the keyframes lie between whole seconds (1.001, 2.002 ...).</summary>
    public string Ntsc => Get("ntsc.mp4", out_ => Encode(out_, X264, "-bf", "2", Rate("30000/1001")));

    public string Fps60 => Get("fps60.mp4", out_ => Encode(out_, X264, "-bf", "2", Rate("60")));

    /// <summary>A 40 s clip with a keyframe every 250 frames (8.33 s).</summary>
    public string LongGop => Get("long_gop.mp4", out_ => Encode(out_, LongGopArgs, Duration(40)));

    /// <summary>A QuickTime MOV: <c>ftyp qt</c>, a <c>wide</c> box, the moov at the end.</summary>
    public string Mov => Get("clip.mov", out_ => Encode(out_, X264, "-bf", "2"));

    /// <summary>A video that starts half a second late next to audio at 0: an MP4 with a leading empty edit (media_time -1).</summary>
    public string LateVideoMp4 => Get("late_video.mp4", out_ => LateVideo(out_));

    /// <summary>The same as a MOV.</summary>
    public string LateVideoMov => Get("late_video.mov", out_ => LateVideo(out_));

    /// <summary>
    /// A stream copy that starts 3.5 s into <see cref="H264Bframes"/>: its edit list starts mid-GOP, so the first GOP's keyframe is
    /// listed before the start (ffprobe: a negative pts, flag <c>KD</c>).
    /// </summary>
    public string TrimmedStart => Get("trimmed_start.mp4", out_ =>
        Run("-y", "-ss", "3.5", "-i", H264Bframes, "-c", "copy", out_));

    /// <summary>
    /// A stream copy of <paramref name="shape"/> (a property name of this class) that starts <paramref name="startSeconds"/> in:
    /// the edit list's media time is where the cut fell, which is not the time of a frame.
    /// </summary>
    public string TrimmedCopy(string shape, double startSeconds)
    {
        var source = (string)typeof(SampleTableFixtures).GetProperty(shape)!.GetValue(this)!;
        var name = $"trim_{shape}_{startSeconds.ToString("0.###", CultureInfo.InvariantCulture)}.mp4";
        return Get(name, out_ => Run("-y", "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", source, "-c", "copy", out_));
    }

    /// <summary>A stream copy cut short at 4.3 s: the edit list's duration is shorter than the media.</summary>
    public string TrimmedEnd => Get("trimmed_end.mp4", out_ =>
        Run("-y", "-i", H264Bframes, "-t", "4.3", "-c", "copy", out_));

    /// <summary>ctts version 1 with negative composition offsets (<c>+negative_cts_offsets</c>).</summary>
    public string NegativeCtsOffsets => Get("negative_cts.mp4", out_ => Encode(out_, X264, "-bf", "2", "-movflags", "+negative_cts_offsets"));

    /// <summary>Fragmented MP4: an empty moov with <c>mvex</c>, then moof/mdat pairs.</summary>
    public string Fragmented => Get("fragmented.mp4", out_ => Encode(out_, X264, "-bf", "2", "-movflags", "frag_keyframe+empty_moov"));

    public string Matroska => Get("clip.mkv", out_ => Run("-y", "-i", H264Bframes, "-c", "copy", out_));

    public string TransportStream => Get("clip.ts", out_ => Run("-y", "-i", H264Bframes, "-c", "copy", out_));

    /// <summary>The MKV under an <c>.mp4</c> name: the reader goes by the bytes, not the name.</summary>
    public string MatroskaNamedMp4 => Get("mislabeled.mp4", out_ => File.Copy(Matroska, out_, overwrite: true));

    /// <summary>The MP4 under a <c>.mkv</c> name.</summary>
    public string Mp4NamedMkv => Get("mislabeled.mkv", out_ => File.Copy(H264Bframes, out_, overwrite: true));

    /// <summary>Two H.264 video tracks; ffprobe's v:0 is the first.</summary>
    public string TwoVideoTracks => Get("two_video_tracks.mp4", out_ =>
        Run("-y", "-f", "lavfi", "-i", "testsrc=duration=10:size=320x240:rate=30", "-f", "lavfi", "-i", "testsrc2=duration=10:size=160x120:rate=25",
            "-map", "0:v", "-map", "1:v", "-c:v", "libx264", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0", "-pix_fmt", "yuv420p", out_));

    /// <summary>An audio track in front of the video track: the reader takes the first VIDEO track, not the first track.</summary>
    public string AudioTrackFirst => Get("audio_first.mp4", out_ =>
        Run("-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=10", "-f", "lavfi", "-i", "testsrc=duration=10:size=320x240:rate=30",
            "-map", "0:a", "-map", "1:v", "-c:a", "aac", "-c:v", "libx264", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0", "-bf", "2", "-pix_fmt", "yuv420p", out_));

    /// <summary>The first video track is MPEG-4 part 2 (an <c>mp4v</c> entry), the second H.264: v:0 is the first, outside the subset.</summary>
    public string FirstTrackMpeg4 => Get("first_track_mpeg4.mp4", out_ =>
        Run("-y", "-f", "lavfi", "-i", "testsrc=duration=10:size=320x240:rate=30", "-f", "lavfi", "-i", "testsrc2=duration=10:size=160x120:rate=25",
            "-map", "0:v", "-map", "1:v", "-c:v:0", "mpeg4", "-g:v:0", "30", "-c:v:1", "libx264", "-g:v:1", "30", "-pix_fmt:v:1", "yuv420p", out_));

    /// <summary>H.264 with open GOPs: the I frames after the first are not IDR pictures.</summary>
    public string OpenGop => Get("open_gop.mp4", out_ => Encode(out_,
        "-c:v", "libx264", "-x264-params", "open-gop=1:keyint=30:min-keyint=30:scenecut=0:bframes=2", "-pix_fmt", "yuv420p"));

    /// <summary>
    /// What a Join writes: a stream-copy concat of clips with AAC audio. The AAC priming packet shifts every stream so the audio
    /// starts at 0 and the video 0.021 s later (<c>format.start_time</c> 0, the video's own start 0.021).
    /// </summary>
    public string JoinStyle => Get("join_style.mp4", out_ =>
    {
        var clip = Get("join_clip.mp4", c => Run("-y", "-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-c:v", "libx264", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0", "-bf", "2", "-pix_fmt", "yuv420p", "-c:a", "aac", c));
        var list = Path.Combine(_dir, "join_list.txt");
        File.WriteAllText(list, string.Join("\n", Enumerable.Repeat($"file '{clip.Replace('\\', '/')}'", 3)) + "\n");
        Run("-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", out_);
    });

    /// <summary>A file of 50 MB or more with the moov at the end: 12 s of 1080p at a constant 36 Mbit/s (x264 filler).</summary>
    public string Large => Get("large_50mb.mp4", out_ =>
        Run("-y", "-f", "lavfi", "-i", "testsrc2=duration=12:size=1920x1080:rate=30", "-c:v", "libx264", "-preset", "ultrafast", "-b:v", "36M", "-minrate", "36M",
            "-maxrate", "36M", "-bufsize", "36M", "-x264-params", "nal-hrd=cbr:keyint=60:min-keyint=60:scenecut=0", "-pix_fmt", "yuv420p", out_));

    /// <summary>A path inside the fixture folder for a test's own file (a copy to patch, a truncated copy).</summary>
    public string PathFor(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; a locked temp file is not a test failure.
        }
    }

    // ---- recipes -------------------------------------------------------------------------------------------------------

    private static readonly string[] X264 =
        ["-c:v", "libx264", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0", "-pix_fmt", "yuv420p"];

    private static readonly string[] X265 =
        ["-c:v", "libx265", "-g", "30", "-keyint_min", "30", "-x265-params", "scenecut=0:log-level=none", "-pix_fmt", "yuv420p"];

    private static readonly string[] X265Main10 =
        ["-c:v", "libx265", "-g", "30", "-keyint_min", "30", "-x265-params", "scenecut=0:log-level=none", "-pix_fmt", "yuv420p10le"];

    private static readonly string[] LongGopArgs =
        ["-c:v", "libx264", "-g", "250", "-keyint_min", "250", "-sc_threshold", "0", "-bf", "2", "-pix_fmt", "yuv420p"];

    private string Get(string name, Action<string> build)
    {
        var lazy = _files.GetOrAdd(name, n => new Lazy<string>(() =>
        {
            var path = Path.Combine(_dir, n);
            build(path);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"ffmpeg reported success but '{path}' was not written.");
            }

            return path;
        }));
        return lazy.Value;
    }

    // A recipe argument that sets the source frame rate (picked up by Encode).
    private static string Rate(string rate) => "rate=" + rate;

    private static string Duration(int seconds) => "duration=" + seconds.ToString(CultureInfo.InvariantCulture);

    /// <summary>Encode the lavfi test pattern (10 s, 320x240, 30 fps unless a <c>rate=</c> / <c>duration=</c> argument says otherwise).</summary>
    private static void Encode(string output, params object[] parts)
    {
        var encoderArgs = new List<string>();
        var rate = "30";
        var duration = "10";
        foreach (var part in parts)
        {
            switch (part)
            {
                case string[] many:
                    encoderArgs.AddRange(many);
                    break;
                case string s when s.StartsWith("rate=", StringComparison.Ordinal):
                    rate = s["rate=".Length..];
                    break;
                case string s when s.StartsWith("duration=", StringComparison.Ordinal):
                    duration = s["duration=".Length..];
                    break;
                case string s:
                    encoderArgs.Add(s);
                    break;
            }
        }

        var args = new List<string> { "-y", "-f", "lavfi", "-i", $"testsrc=duration={duration}:size=320x240:rate={rate}" };
        args.AddRange(encoderArgs);
        args.Add(output);
        Run(args.ToArray());
    }

    private static void LateVideo(string output) =>
        Run("-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=10", "-itsoffset", "0.5", "-f", "lavfi", "-i", "testsrc=duration=10:size=320x240:rate=30",
            "-map", "1:v", "-map", "0:a", "-c:v", "libx264", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0", "-bf", "2", "-pix_fmt", "yuv420p", "-c:a", "aac", output);

    /// <summary>Run ffmpeg with the arguments (the output path last); throws when it fails.</summary>
    internal static void Run(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegTestBinaries.FfmpegRequired,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = new Process { StartInfo = psi };
        p.Start();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed (exit {p.ExitCode}) for {string.Join(' ', args)}:{Environment.NewLine}{stderr}");
        }
    }
}

/// <summary>
/// An <see cref="IFfprobeRunner"/> that runs the real ffprobe and records the arguments of every run, so a test can say what the
/// code asked ffprobe for: how many runs, and whether any was a whole-file packet scan (T-194).
/// </summary>
internal sealed class ArgsRecordingFfprobeRunner : IFfprobeRunner
{
    private readonly IFfprobeRunner _inner;
    private readonly object _lock = new();
    private readonly List<IReadOnlyList<string>> _runs = new();
    private readonly List<string> _outputs = new();

    public ArgsRecordingFfprobeRunner(IFfprobeRunner inner)
    {
        _inner = inner;
    }

    public ArgsRecordingFfprobeRunner()
        : this(new FfprobeRunner(new FfmpegBinaryLocator(ffprobeOverride: FfmpegTestBinaries.Ffprobe)))
    {
    }

    /// <summary>The arguments of every run so far, in the order they started.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Runs
    {
        get
        {
            lock (_lock)
            {
                return _runs.ToList();
            }
        }
    }

    /// <summary>Runs that read the packets (<c>-show_packets</c>) without <c>-read_intervals</c>: scans of the whole file.</summary>
    public IReadOnlyList<IReadOnlyList<string>> WholeFilePacketScans =>
        Runs.Where(a => a.Contains("-show_packets") && !a.Contains("-read_intervals")).ToList();

    /// <summary>What each run printed (a run that threw has no entry), for a test's diagnostics.</summary>
    public IReadOnlyList<string> Outputs
    {
        get
        {
            lock (_lock)
            {
                return _outputs.ToList();
            }
        }
    }

    public async Task<string> RunJsonAsync(FfmpegArgs args, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _runs.Add(args.ToList().ToArray());
        }

        var output = await _inner.RunJsonAsync(args, ct).ConfigureAwait(false);
        lock (_lock)
        {
            _outputs.Add(output);
        }

        return output;
    }
}

/// <summary>Helpers shared by the T-194 sample-table tests.</summary>
internal static class SampleTableTestSupport
{
    public static MediaProbe NewProbe(IFfprobeRunner? runner = null) =>
        new(runner ?? new FfprobeRunner(new FfmpegBinaryLocator(ffprobeOverride: FfmpegTestBinaries.Ffprobe)));
}
