using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using VideoSplitJoiner.App.Media;
using VideoSplitJoiner.App.Settings;
using VideoSplitJoiner.Core.Ffmpeg;
using VideoSplitJoiner.Core.Join;
using VideoSplitJoiner.Core.Media;
using VideoSplitJoiner.App.Io;
using VideoSplitJoiner.Core.Split;
using VideoSplitJoiner.Core.Thumbnails;
using VideoSplitJoiner.Core.Waveform;

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

    /// <summary>
    /// Fallback ratios for the Bulk Cut tab's OWN split (G-039 / T-112) — the preview-pane fraction.
    /// Side-by-side (horizontal) leans the pane smaller so the wide row-list gets the extra width;
    /// stacked (vertical) is a balanced ~50/50. Kept separate from the Split-tab defaults above.
    /// </summary>
    private const double DefaultBulkHorizontalRatio = 0.4;
    private const double DefaultBulkVerticalRatio = 0.5;

    private bool _isVertical;
    private double _horizontalSplitRatio = DefaultHorizontalRatio;
    private double _verticalSplitRatio = DefaultVerticalRatio;
    private double _bulkHorizontalSplitRatio = DefaultBulkHorizontalRatio;
    private double _bulkVerticalSplitRatio = DefaultBulkVerticalRatio;

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
        // T-122: a replaced original goes to the Recycle Bin, so OutputMode.ReplaceOriginal stays undoable.
        var splitEngine = new SplitEngine(ffmpegRunner, probe, new RecycleBinOriginalDisposer());
        var joinEngine = new JoinEngine(ffmpegRunner, probe);

        // T-077/T-078: the scrub-bar hover thumbnail source — a separate ffmpeg CLI process per frame
        // (own bucket-cached temp files), independent of the FFME preview. Shared into the Split screen's
        // player VM, which debounces hover + shows the popup.
        var thumbnailService = new FfmpegThumbnailService(ffmpegRunner);

        // T-084/D-002: the audio-waveform source — a separate ffmpeg CLI pass that extracts a downsampled
        // mono PCM temp file and reduces it to a normalized peak array (own per-file LRU cache), built over
        // the same runner as the thumbnail/split services. Threaded into the Split screen so LoadAsync can
        // extract the wave in the background; a null-audio / failed extraction simply hides the band.
        var waveformService = new FfmpegWaveformService(ffmpegRunner);

        // Cross-session folder memory (T-038) — one shared file-backed store so both screens read/write
        // the same %APPDATA%/VideoSplitJoiner/settings.json (last input + last output folders). The same
        // store also carries the D-001 layout mode + per-axis split ratios (T-081).
        var settings = new AppSettings();
        _settings = settings;
        _isVertical = settings.LayoutMode == LayoutMode.Vertical;   // restore last-used axis on startup
        // T-143: restore the last-used screen beside the axis. Assigned to the FIELD, not the property,
        // so restoring does not immediately write the same value back to disk - and, more importantly,
        // so restoring a tab does not run the tab's WORK: the property setter stops the other screens'
        // players and re-points the operation surface, which is switching behaviour, not startup state.
        _selectedTabIndex = (int)(settings.LastTab ?? AppTab.Split);

        _horizontalSplitRatio = settings.HorizontalSplitRatio ?? DefaultHorizontalRatio;
        _verticalSplitRatio = settings.VerticalSplitRatio ?? DefaultVerticalRatio;
        _bulkHorizontalSplitRatio = settings.BulkHorizontalSplitRatio ?? DefaultBulkHorizontalRatio;
        _bulkVerticalSplitRatio = settings.BulkVerticalSplitRatio ?? DefaultBulkVerticalRatio;

        // The in-app preview player is FFME-backed (FfmeMediaPlayer, decodes via ffmpeg); PlayerView
        // attaches its FFME MediaElement on Loaded. Unattached here, so construction stays render-free.
        // T-162 (G-052): the disposer for "Delete original". Passed HERE rather than defaulted inside
        // the view model, for the same reason Bulk Cut's is (T-144/T-140) — null means the feature is
        // simply unavailable, so a test that forgets to inject one can never bin real files. The flip
        // side is that forgetting it HERE leaves the feature inert in the shipped app while every test
        // still passes, which is exactly what happened on the first pass of T-162.
        Split = new SplitViewModel(
            probe, splitEngine, new FfmeMediaPlayer(), settings, thumbnailService, waveformService,
            originalDisposer: new RecycleBinOriginalDisposer());
        Join = new JoinViewModel(joinEngine, probe, settings);

        // D-004 / T-097 — the Bulk Cut screen shares the SAME probe / split engine / thumbnail service /
        // settings instances (no second ffmpeg graph); its batch runner defaults over the shared split engine.
        // T-100 — it gets its OWN FfmeMediaPlayer for the shared mini-preview (a second FFME element is
        // fine; Split/Bulk are different tabs and only the active tab decodes — see StopInactiveScreenPlayers).
        // T-125 (G-042): give Bulk Cut a smart-cut engine so "Exact cut" can honour the requested
        // time by re-encoding ~1 GOP; without it the mode silently stays lossless.
        BulkCut = new BulkCutViewModel(
            probe, splitEngine, thumbnailService, settings,
            player: new FfmeMediaPlayer(),
            smartCut: new SmartCutEngine(ffmpegRunner, probe),
            // T-144: the disposer for "Delete originals". Passed HERE rather than defaulted inside the
            // view model on purpose - null means the feature is unavailable, so a test that forgets to
            // inject one can never bin real files (the T-140 lesson).
            originalDisposer: new RecycleBinOriginalDisposer());

        ToggleLayoutCommand = new RelayCommand(ToggleLayout);

        HookOperations();
    }

    /// <summary>
    /// Test-friendly ctor: inject already-composed screen view models (join optional) and, optionally,
    /// a settings store so the layout toggle's write-through + startup restore can be exercised without
    /// the production ffmpeg graph. When <paramref name="settings"/> is null the toggle flips
    /// <see cref="IsVertical"/> in memory only.
    /// </summary>
    public MainViewModel(SplitViewModel splitViewModel, JoinViewModel? joinViewModel = null, IAppSettings? settings = null, BulkCutViewModel? bulkCut = null)
    {
        Split = splitViewModel ?? throw new ArgumentNullException(nameof(splitViewModel));
        Join = joinViewModel!;
        BulkCut = bulkCut!;

        _settings = settings;
        if (settings is not null)
        {
            _isVertical = settings.LayoutMode == LayoutMode.Vertical;
            _selectedTabIndex = (int)(settings.LastTab ?? AppTab.Split);   // T-143
            _horizontalSplitRatio = settings.HorizontalSplitRatio ?? DefaultHorizontalRatio;
            _verticalSplitRatio = settings.VerticalSplitRatio ?? DefaultVerticalRatio;
            _bulkHorizontalSplitRatio = settings.BulkHorizontalSplitRatio ?? DefaultBulkHorizontalRatio;
            _bulkVerticalSplitRatio = settings.BulkVerticalSplitRatio ?? DefaultBulkVerticalRatio;
        }

        ToggleLayoutCommand = new RelayCommand(ToggleLayout);

        HookOperations();
    }

    /// <summary>The Split screen view model, bound by the Split tab.</summary>
    public SplitViewModel Split { get; }

    /// <summary>The Join screen view model, bound by the Join tab.</summary>
    public JoinViewModel Join { get; }

    /// <summary>The Bulk Cut screen view model, bound by the Bulk Cut tab (D-004 / T-097).</summary>
    public BulkCutViewModel BulkCut { get; }

    /// <summary>
    /// Which screen tab is active (0 = Split, 1 = Join, 2 = Bulk Cut). Two-way bound to the TabControl's
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
                // T-143: remember the screen, beside the layout axis. Stored as a NAMED value, so
                // reordering the tabs later cannot silently point it at the wrong screen.
                if (_settings is not null && Enum.IsDefined(typeof(AppTab), value))
                {
                    _settings.LastTab = (AppTab)value;
                }

                // T-100 — two FFME elements (Split + Bulk) live in one process; only the active tab
                // should decode, so stop the players of the now-inactive screens on every switch.
                StopInactiveScreenPlayers();
                OnPropertyChanged(nameof(CurrentOperation));
                // T-088 — the shared tab-strip Load/Clear buttons follow the active screen.
                OnPropertyChanged(nameof(CurrentClearCommand));
                OnPropertyChanged(nameof(CurrentLoadLabel));
                OnPropertyChanged(nameof(CurrentClearLabel));
                OnPropertyChanged(nameof(CurrentLoadTooltip));
                OnPropertyChanged(nameof(CurrentClearTooltip));
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
        => IsBulkActive ? BulkCut.Operation
            : (IsJoinActive ? Join.Operation : Split.Operation);

    /// <summary>
    /// True when the Join screen is the active tab (tab 1) — the single source of truth the
    /// current-load/clear routing below reads. Falls back to Split when Join is absent (test ctor).
    /// </summary>
    private bool IsJoinActive => SelectedTabIndex == 1 && Join is not null;

    /// <summary>
    /// True when the Bulk Cut screen is the active tab (tab 2, D-004 / T-097). Falls back to Split when
    /// <see cref="BulkCut"/> is absent (legacy 3-arg test ctor) so tab-2 routing never throws.
    /// </summary>
    private bool IsBulkActive => SelectedTabIndex == 2 && BulkCut is not null;

    /// <summary>
    /// The Clear command of the CURRENTLY-ACTIVE screen — Split's <c>ClearCommand</c> on tab 0, Join's
    /// "Clear all" on tab 1 (T-088). The shared tab-strip "Clear" button binds here so it resets
    /// whichever screen the user is on. Each screen's <c>ClearCommand</c> is self-guarded (<c>CanClear</c>:
    /// file/clips present AND no op running), so the shared button disables during a running op
    /// automatically. Re-raised when <see cref="SelectedTabIndex"/> flips.
    /// </summary>
    public ICommand CurrentClearCommand => IsBulkActive
        ? BulkCut.ClearCommand
        : (IsJoinActive ? Join.ClearCommand : Split.ClearCommand);

    /// <summary>
    /// The label for the tab-strip "Load" button, following the active screen: "Load…" on Split,
    /// "Add files…" on Join (T-088). The load picker itself is invoked from a thin MainWindow-level
    /// handler that dispatches to the active view's existing <c>OpenFileDialog</c> logic — the button
    /// is a code-behind <c>Click</c>, not a bound command, since the picker lives in the view.
    /// </summary>
    public string CurrentLoadLabel => IsBulkActive ? "Add videos…" : (IsJoinActive ? "Add files…" : "Load…");

    /// <summary>The label for the tab-strip "Clear" button: "Clear" on Split, "Clear all" on Join/Bulk (T-088 / T-097).</summary>
    public string CurrentClearLabel => IsBulkActive ? "Clear all" : (IsJoinActive ? "Clear all" : "Clear");

    /// <summary>Tooltip for the tab-strip Load button, following the active screen (T-088 / T-097).</summary>
    public string CurrentLoadTooltip => IsBulkActive
        ? "Add videos to bulk-trim their intro/outro"
        : (IsJoinActive
            ? "Add one or more video clips to the join queue"
            : "Open a video file to split");

    /// <summary>Tooltip for the tab-strip Clear button, following the active screen (T-088 / T-097).</summary>
    public string CurrentClearTooltip => IsBulkActive
        ? "Remove all videos and reset the Bulk Cut screen"
        : (IsJoinActive
            ? "Remove all queued clips and reset the Join screen"
            : "Unload the current file and reset the Split screen");

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

    /// <summary>
    /// The remembered split position for the Bulk Cut tab's HORIZONTAL (side-by-side) layout — the
    /// preview-pane fraction of the total width (0..1). Two-way bound from the Bulk tab's
    /// <c>OrientedSplitPanel</c> so a drag persists here (write-through to
    /// <see cref="IAppSettings.BulkHorizontalSplitRatio"/>). Deliberately SEPARATE from
    /// <see cref="HorizontalSplitRatio"/> (Split's video↔tools ratio) so the two tabs' splits never
    /// couple (G-039 / T-112). Independent of <see cref="BulkVerticalSplitRatio"/> per axis (D6).
    /// </summary>
    public double BulkHorizontalSplitRatio
    {
        get => _bulkHorizontalSplitRatio;
        set
        {
            var clamped = Math.Clamp(value, 0.05, 0.95);
            if (SetProperty(ref _bulkHorizontalSplitRatio, clamped))
            {
                if (_settings is not null)
                {
                    _settings.BulkHorizontalSplitRatio = clamped;
                }
            }
        }
    }

    /// <summary>
    /// The remembered split position for the Bulk Cut tab's VERTICAL (stacked) layout — the
    /// preview-pane fraction of the total height (0..1). Two-way bound + write-through to
    /// <see cref="IAppSettings.BulkVerticalSplitRatio"/>. Separate from <see cref="VerticalSplitRatio"/>
    /// (Split's ratio) and independent of <see cref="BulkHorizontalSplitRatio"/> (D6 / T-112).
    /// </summary>
    public double BulkVerticalSplitRatio
    {
        get => _bulkVerticalSplitRatio;
        set
        {
            var clamped = Math.Clamp(value, 0.05, 0.95);
            if (SetProperty(ref _bulkVerticalSplitRatio, clamped))
            {
                if (_settings is not null)
                {
                    _settings.BulkVerticalSplitRatio = clamped;
                }
            }
        }
    }

    /// <summary>Flip the layout axis (invoked by <see cref="ToggleLayoutCommand"/>).</summary>
    private void ToggleLayout() => IsVertical = !IsVertical;

    /// <summary>
    /// Only the active tab decodes (T-100). The Split and Bulk screens each own an FFME preview element;
    /// leaving one visible-but-inactive would keep a second decoder alive. On every tab switch, stop the
    /// player of each screen that is NOT the active tab (Split = tab 0, Bulk = tab 2; Join has no
    /// player). Idempotent + null-safe: an inert <see cref="NullMediaPlayer"/> (tests) or an unattached
    /// <see cref="FfmeMediaPlayer"/> (before its view is loaded) both no-op, and a legacy test ctor with
    /// no <see cref="BulkCut"/> is guarded.
    /// </summary>
    private void StopInactiveScreenPlayers()
    {
        if (SelectedTabIndex != 0)
        {
            Split.Player.Stop();
        }

        if (!IsBulkActive)
        {
            BulkCut?.Player.Stop();
        }
    }

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

        if (BulkCut is not null)
        {
            BulkCut.Operation.PropertyChanged += OnOperationChanged;
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
