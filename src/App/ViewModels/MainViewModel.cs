using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.Core.Split;
using VideoSplitJoiner.Core.Thumbnails;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Root view model for the main window. Doubles as the app's composition root: it wires the real
/// Core services (locator → ffmpeg/ffprobe runners → probe → split/join engines) into the screen
/// view models. The parameterless ctor builds the production graph; the DI-style ctor lets tests
/// inject fakes.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>
    /// The plain window/caption title shown when nothing is running. The caption row binds to this
    /// (via <see cref="BaseTitle"/>) so the in-app caption always reads the app name; the OS
    /// <c>Window.Title</c> (taskbar / alt-tab) binds to <see cref="WindowTitle"/>, which overlays
    /// the running % + ETA while an operation runs (T-068).
    /// </summary>
    public const string BaseTitle = "Video Split / Join";

    private int _selectedTabIndex;

    /// <summary>
    /// The persisted-settings write-through for the layout toggle (D-001 / T-081). Non-null in the
    /// production ctor; null in the test ctor (which injects screen VMs directly and has no settings
    /// store) — the toggle then flips <see cref="IsVertical"/> in memory only, which is all the tests
    /// assert. The initial <see cref="IsVertical"/> is seeded from this store so the app reopens in
    /// the last-used mode.
    /// </summary>
    private readonly IAppSettings? _settings;

    /// <summary>Fallback split ratios when nothing is persisted (D-001 recommendation: 62% top in vertical).</summary>
    private const double DefaultHorizontalRatio = 0.7;
    private const double DefaultVerticalRatio = 0.62;

    private bool _isVertical;
    private double _horizontalSplitRatio = DefaultHorizontalRatio;
    private double _verticalSplitRatio = DefaultVerticalRatio;

    /// <summary>Production composition root — builds the real ffmpeg-backed Core service graph.</summary>
    public MainViewModel()
    {
        // Hand-wire the concrete Core services once, then share the probe across both screens.
        // FfmpegBinaryLocator resolves ffmpeg/ffprobe (app-local ffmpeg/ folder, then PATH); the
        // runners, probe, and engines are layered on top exactly as the Core tests compose.
        var locator = new FfmpegBinaryLocator();
        var ffprobeRunner = new FfprobeRunner(locator);
        var ffmpegRunner = new FfmpegRunner(locator);

        var probe = new MediaProbe(ffprobeRunner);
        var splitEngine = new SplitEngine(ffmpegRunner, probe);
        var joinEngine = new JoinEngine(ffmpegRunner, probe);

        // T-077/T-078: the scrub-bar hover thumbnail source — a separate ffmpeg CLI process per frame
        // (own bucket-cached temp files), independent of the FFME preview. Shared into the Split screen's
        // player VM, which debounces hover + shows the popup.
        var thumbnailService = new FfmpegThumbnailService(ffmpegRunner);

        // Cross-session folder memory (T-038) — one shared file-backed store so both screens read/write
        // the same %APPDATA%/VideoSplitJoiner/settings.json (last input + last output folders). The same
        // store also carries the D-001 layout mode + per-axis split ratios (T-081).
        var settings = new AppSettings();
        _settings = settings;
        _isVertical = settings.LayoutMode == LayoutMode.Vertical;   // restore last-used axis on startup
        _horizontalSplitRatio = settings.HorizontalSplitRatio ?? DefaultHorizontalRatio;
        _verticalSplitRatio = settings.VerticalSplitRatio ?? DefaultVerticalRatio;

        // The in-app preview player is FFME-backed (FfmeMediaPlayer, decodes via ffmpeg); PlayerView
        // attaches its FFME MediaElement on Loaded. Unattached here, so construction stays render-free.
        Split = new SplitViewModel(probe, splitEngine, new FfmeMediaPlayer(), settings, thumbnailService);
        Join = new JoinViewModel(joinEngine, probe, settings);

        ToggleLayoutCommand = new RelayCommand(ToggleLayout);

        HookOperations();
    }

    /// <summary>
    /// Test-friendly ctor: inject already-composed screen view models (join optional) and, optionally,
    /// a settings store so the layout toggle's write-through + startup restore can be exercised without
    /// the production ffmpeg graph. When <paramref name="settings"/> is null the toggle flips
    /// <see cref="IsVertical"/> in memory only.
    /// </summary>
    public MainViewModel(SplitViewModel splitViewModel, JoinViewModel? joinViewModel = null, IAppSettings? settings = null)
    {
        Split = splitViewModel ?? throw new ArgumentNullException(nameof(splitViewModel));
        Join = joinViewModel!;

        _settings = settings;
        if (settings is not null)
        {
            _isVertical = settings.LayoutMode == LayoutMode.Vertical;
            _horizontalSplitRatio = settings.HorizontalSplitRatio ?? DefaultHorizontalRatio;
            _verticalSplitRatio = settings.VerticalSplitRatio ?? DefaultVerticalRatio;
        }

        ToggleLayoutCommand = new RelayCommand(ToggleLayout);

        HookOperations();
    }

    /// <summary>The Split screen view model, bound by the Split tab.</summary>
    public SplitViewModel Split { get; }

    /// <summary>The Join screen view model, bound by the Join tab.</summary>
    public JoinViewModel Join { get; }

    /// <summary>
    /// Which screen tab is active (0 = Split, 1 = Join). Two-way bound to the TabControl's
    /// <c>SelectedIndex</c>. Changing it re-points <see cref="CurrentOperation"/> at the newly
    /// selected screen's operation and recomputes the <see cref="WindowTitle"/>.
    /// </summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
            {
                OnPropertyChanged(nameof(CurrentOperation));
                RaiseWindowTitle();
            }
        }
    }

    /// <summary>
    /// The operation of the CURRENTLY-ACTIVE screen (T-068). The taskbar progress binding
    /// (<c>Window.TaskbarItemInfo</c>) follows this so the taskbar button reflects whichever screen
    /// the user is on. Falls back to the Split operation when Join is absent (test ctor).
    /// </summary>
    public OperationViewModel CurrentOperation
        => (SelectedTabIndex == 1 && Join is not null) ? Join.Operation : Split.Operation;

    /// <summary>
    /// The OS <c>Window.Title</c> (shown on the taskbar hover / alt-tab). While the active screen's
    /// operation runs, this overlays the short verb + overall % + ETA, e.g.
    /// "Splitting 45% · ~1m 20s — Video Split / Join"; otherwise it is the plain
    /// <see cref="BaseTitle"/>. This is how the ETA becomes visible from the taskbar (the button
    /// itself can't render text). It is intentionally separate from the caption title, which stays
    /// on <see cref="BaseTitle"/> so the in-app caption row never flickers with progress.
    /// </summary>
    public string WindowTitle => ComposeWindowTitle(CurrentOperation);

    /// <summary>
    /// The fixed caption-row title (always the plain app name). Bound by the in-app caption so it
    /// never shows the running-progress overlay that <see cref="WindowTitle"/> puts on the OS title.
    /// Instance property (not the <see cref="BaseTitle"/> const) so it is XAML-bindable.
    /// </summary>
    public string CaptionTitle => BaseTitle;

    /// <summary>
    /// The app-wide layout axis (D-001 / T-081): <c>false</c> = the original horizontal two-column
    /// layout, <c>true</c> = the vertical stacked layout (video/timeline on top, tools below). Both
    /// the Split and Join views observe this (they bind up to the owning <see cref="MainViewModel"/>)
    /// so the single caption toggle flips both screens together. Setting it writes through to
    /// <see cref="IAppSettings.LayoutMode"/> so the choice persists across launches, and re-raises the
    /// icon/tooltip helpers. Seeded from the persisted setting on startup.
    /// </summary>
    public bool IsVertical
    {
        get => _isVertical;
        set
        {
            if (SetProperty(ref _isVertical, value))
            {
                // Persist through to settings (write-through), best-effort — the store never throws.
                if (_settings is not null)
                {
                    _settings.LayoutMode = value ? LayoutMode.Vertical : LayoutMode.Horizontal;
                }

                OnPropertyChanged(nameof(LayoutToggleTooltip));
            }
        }
    }

    /// <summary>
    /// The caption toggle command (D5) — flips <see cref="IsVertical"/> and, in production, writes the
    /// new mode through to settings. Bound to the title-bar toggle button.
    /// </summary>
    public ICommand ToggleLayoutCommand { get; }

    /// <summary>
    /// Tooltip for the caption toggle button — names the mode the click will switch <em>to</em> (D5),
    /// matching the target-orientation icon: "Switch to vertical layout" while horizontal, "Switch to
    /// horizontal layout" while vertical.
    /// </summary>
    public string LayoutToggleTooltip =>
        IsVertical ? "Switch to horizontal layout" : "Switch to vertical layout";

    /// <summary>
    /// The remembered split position for the HORIZONTAL layout — the video column's fraction of the
    /// total width (0..1). Two-way bound from both screens' split panels so a drag in either axis
    /// persists here (write-through to <see cref="IAppSettings.HorizontalSplitRatio"/>). Kept separate
    /// from <see cref="VerticalSplitRatio"/> so a flip never distorts the other axis (D6).
    /// </summary>
    public double HorizontalSplitRatio
    {
        get => _horizontalSplitRatio;
        set
        {
            var clamped = Math.Clamp(value, 0.05, 0.95);
            if (SetProperty(ref _horizontalSplitRatio, clamped))
            {
                if (_settings is not null)
                {
                    _settings.HorizontalSplitRatio = clamped;
                }
            }
        }
    }

    /// <summary>
    /// The remembered split position for the VERTICAL layout — the video/timeline block's fraction of
    /// the total height (0..1, default ≈0.62). Two-way bound + write-through to
    /// <see cref="IAppSettings.VerticalSplitRatio"/>. Independent of <see cref="HorizontalSplitRatio"/> (D6).
    /// </summary>
    public double VerticalSplitRatio
    {
        get => _verticalSplitRatio;
        set
        {
            var clamped = Math.Clamp(value, 0.05, 0.95);
            if (SetProperty(ref _verticalSplitRatio, clamped))
            {
                if (_settings is not null)
                {
                    _settings.VerticalSplitRatio = clamped;
                }
            }
        }
    }

    /// <summary>Flip the layout axis (invoked by <see cref="ToggleLayoutCommand"/>).</summary>
    private void ToggleLayout() => IsVertical = !IsVertical;

    /// <summary>
    /// Composes the running-title overlay from an operation's status/progress/ETA, or the plain
    /// <see cref="BaseTitle"/> when it is not running. Static + pure so it is unit-testable.
    /// Example: (Running, "Splitting…", 0.45, "~1m 20s left") → "Splitting 45% · ~1m 20s — Video Split / Join".
    /// </summary>
    public static string ComposeWindowTitle(OperationViewModel? operation)
    {
        if (operation is null || !operation.IsRunning)
        {
            return BaseTitle;
        }

        var verb = ShortVerb(operation.StatusText);
        var percent = (int)Math.Round(Math.Clamp(operation.Progress, 0d, 1d) * 100d, MidpointRounding.AwayFromZero);
        var eta = ShortEta(operation.EtaText);

        // "Splitting 45%" — always present while running (verb + overall %).
        var lead = string.IsNullOrEmpty(verb)
            ? string.Format(CultureInfo.InvariantCulture, "{0}%", percent)
            : string.Format(CultureInfo.InvariantCulture, "{0} {1}%", verb, percent);

        // Append the ETA only when it is a real number (not "estimating…"): "… · ~1m 20s".
        var prefix = string.IsNullOrEmpty(eta)
            ? lead
            : $"{lead} · {eta}";

        return $"{prefix} — {BaseTitle}";
    }

    /// <summary>
    /// The short leading verb for the title: the status line with its trailing "…" and any
    /// "(detail)" suffix stripped — "Splitting… (4 parts)" → "Splitting", "Preparing…" → "Preparing".
    /// </summary>
    private static string ShortVerb(string? statusText)
    {
        if (string.IsNullOrWhiteSpace(statusText))
        {
            return string.Empty;
        }

        var text = statusText;

        // Drop a "(detail)" suffix — the title carries the % separately.
        var paren = text.IndexOf('(');
        if (paren >= 0)
        {
            text = text.Substring(0, paren);
        }

        // Drop the trailing ellipsis / whitespace.
        return text.Replace("…", string.Empty, StringComparison.Ordinal).Trim();
    }

    /// <summary>
    /// The compact ETA for the title: the friendly EtaText with its trailing " left" dropped
    /// ("~1m 20s left" → "~1m 20s"). Returns empty for the "estimating…" placeholder / null so the
    /// title shows just "verb %" until a real estimate exists.
    /// </summary>
    private static string ShortEta(string? etaText)
    {
        if (string.IsNullOrWhiteSpace(etaText) || !etaText.StartsWith("~", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        const string suffix = " left";
        return etaText.EndsWith(suffix, StringComparison.Ordinal)
            ? etaText.Substring(0, etaText.Length - suffix.Length)
            : etaText;
    }

    /// <summary>
    /// Subscribe to both screens' operations so the window title (and the current-operation taskbar
    /// binding, when the active screen's op changes state) refresh as progress/status/ETA move.
    /// Both are watched — only the active screen's op actually drives the title, but the inactive
    /// screen sits idle so its notifications are harmless and switching tabs stays live.
    /// </summary>
    private void HookOperations()
    {
        Split.Operation.PropertyChanged += OnOperationChanged;
        if (Join is not null)
        {
            Join.Operation.PropertyChanged += OnOperationChanged;
        }
    }

    private void OnOperationChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only the properties the title is composed from need to re-raise it.
        if (e.PropertyName is nameof(OperationViewModel.State)
            or nameof(OperationViewModel.IsRunning)
            or nameof(OperationViewModel.Progress)
            or nameof(OperationViewModel.StatusText)
            or nameof(OperationViewModel.EtaText))
        {
            RaiseWindowTitle();
        }
    }

    private void RaiseWindowTitle() => OnPropertyChanged(nameof(WindowTitle));
}
