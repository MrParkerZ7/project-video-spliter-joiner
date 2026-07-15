using VideoSplitJoiner.Core;
using VideoSplitJoiner.Core.Detect;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Root view model for the main window. Doubles as the app's composition root: it wires the real
/// Core services (locator → ffmpeg/ffprobe runners → probe → split engine + detector) into the
/// screen view models. The parameterless ctor builds the production graph; the DI-style ctor lets
/// tests inject fakes.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private string _title = $"{AppInfo.Name} — Split / Join";

    /// <summary>Production composition root — builds the real ffmpeg-backed Core service graph.</summary>
    public MainViewModel()
        : this(BuildSplitViewModel())
    {
    }

    /// <summary>Test-friendly ctor: inject an already-composed <see cref="SplitViewModel"/>.</summary>
    public MainViewModel(SplitViewModel splitViewModel)
    {
        Split = splitViewModel ?? throw new System.ArgumentNullException(nameof(splitViewModel));
    }

    /// <summary>Window title text, bindable from the view.</summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>The Split screen view model, bound by the Split tab.</summary>
    public SplitViewModel Split { get; }

    /// <summary>
    /// Hand-wire the concrete Core services. The <see cref="FfmpegBinaryLocator"/> resolves ffmpeg /
    /// ffprobe (app-local <c>ffmpeg/</c> folder, then PATH); the runners, probe, split engine, and
    /// detector are layered on top exactly as the Core unit tests compose them.
    /// </summary>
    private static SplitViewModel BuildSplitViewModel()
    {
        var locator = new FfmpegBinaryLocator();
        var ffprobeRunner = new FfprobeRunner(locator);
        var ffmpegRunner = new FfmpegRunner(locator);

        var probe = new MediaProbe(ffprobeRunner);
        var splitEngine = new SplitEngine(ffmpegRunner, probe);
        var detector = new SplitPointDetector(ffmpegRunner, probe, locator);

        return new SplitViewModel(probe, splitEngine, detector);
    }
}
