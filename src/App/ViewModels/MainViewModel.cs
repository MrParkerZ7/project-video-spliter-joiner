using VideoSplitJoiner.Core;
using VideoSplitJoiner.Core.Detect;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Root view model for the main window. Doubles as the app's composition root: it wires the real
/// Core services (locator → ffmpeg/ffprobe runners → probe → split/join engines + detector) into the
/// screen view models. The parameterless ctor builds the production graph; the DI-style ctor lets
/// tests inject fakes.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private string _title = $"{AppInfo.Name} — Split / Join";

    /// <summary>Production composition root — builds the real ffmpeg-backed Core service graph.</summary>
    public MainViewModel()
    {
        // Hand-wire the concrete Core services once, then share the probe across both screens.
        // FfmpegBinaryLocator resolves ffmpeg/ffprobe (app-local ffmpeg/ folder, then PATH); the
        // runners, probe, engines, and detector are layered on top exactly as the Core tests compose.
        var locator = new FfmpegBinaryLocator();
        var ffprobeRunner = new FfprobeRunner(locator);
        var ffmpegRunner = new FfmpegRunner(locator);

        var probe = new MediaProbe(ffprobeRunner);
        var splitEngine = new SplitEngine(ffmpegRunner, probe);
        var detector = new SplitPointDetector(ffmpegRunner, probe, locator);
        var joinEngine = new JoinEngine(ffmpegRunner, probe);

        Split = new SplitViewModel(probe, splitEngine, detector);
        Join = new JoinViewModel(joinEngine, probe);
    }

    /// <summary>Test-friendly ctor: inject already-composed screen view models (join optional).</summary>
    public MainViewModel(SplitViewModel splitViewModel, JoinViewModel? joinViewModel = null)
    {
        Split = splitViewModel ?? throw new System.ArgumentNullException(nameof(splitViewModel));
        Join = joinViewModel!;
    }

    /// <summary>Window title text, bindable from the view.</summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>The Split screen view model, bound by the Split tab.</summary>
    public SplitViewModel Split { get; }

    /// <summary>The Join screen view model, bound by the Join tab.</summary>
    public JoinViewModel Join { get; }
}
