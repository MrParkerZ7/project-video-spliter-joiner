using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Projection view model for the timeline overlay strip (T-014). Sits over an owning
/// <see cref="SplitViewModel"/> and turns its <see cref="SplitViewModel.Player"/> position/duration,
/// <see cref="SplitViewModel.Markers"/> and <see cref="SplitViewModel.Candidates"/> into a flat,
/// bindable set of normalized ticks + a playhead position for the track view. All logic is WPF-free
/// so it is fully unit-testable with fakes.
///
/// <para>Re-projection is event-driven: it observes the marker/candidate collections'
/// <see cref="INotifyCollectionChanged"/> and the player's Position/Duration changes, refreshing the
/// tick lists + playhead whenever anything moves. Clicking a track position routes through the owner's
/// existing <see cref="SplitViewModel.AddCutAt"/> (no new snap logic); clicking a tick routes to the
/// owner's existing <see cref="SplitViewModel.SeekToMarkerCommand"/> /
/// <see cref="SplitViewModel.PreviewCandidateCommand"/> (no new seek logic).</para>
/// </summary>
public sealed class TimelineViewModel : ObservableObject
{
    private readonly SplitViewModel _owner;

    private IReadOnlyList<TimelineTick> _markerTicks = Array.Empty<TimelineTick>();
    private IReadOnlyList<TimelineTick> _candidateTicks = Array.Empty<TimelineTick>();
    private double _playheadNormalized;

    /// <summary>Wrap <paramref name="owner"/> and subscribe to the sources that drive re-projection.</summary>
    public TimelineViewModel(SplitViewModel owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        _owner.Markers.CollectionChanged += OnCollectionChanged;
        _owner.Candidates.CollectionChanged += OnCollectionChanged;
        _owner.Player.PropertyChanged += OnPlayerChanged;

        ClickAtCommand = new RelayCommand(p => { if (p is double x) ClickAt(x); });
        SeekMarkerTickCommand = new RelayCommand(p => { if (p is TimelineTick tick) SeekMarkerTick(tick); });
        PreviewCandidateTickCommand = new RelayCommand(p => { if (p is TimelineTick tick) PreviewCandidateTick(tick); });

        Reproject();
    }

    // ---- State ------------------------------------------------------------------------------

    /// <summary>The player playhead as a fraction of duration in [0,1] (0 when duration unknown).</summary>
    public double PlayheadNormalized
    {
        get => _playheadNormalized;
        private set => SetProperty(ref _playheadNormalized, value);
    }

    /// <summary>One tick per cut marker, positioned by its snapped time.</summary>
    public IReadOnlyList<TimelineTick> MarkerTicks
    {
        get => _markerTicks;
        private set => SetProperty(ref _markerTicks, value);
    }

    /// <summary>One tick per detected candidate, positioned by its (raw) detected time and carrying its kind.</summary>
    public IReadOnlyList<TimelineTick> CandidateTicks
    {
        get => _candidateTicks;
        private set => SetProperty(ref _candidateTicks, value);
    }

    /// <summary>True once a file is loaded and the player duration is known — gates track clicks.</summary>
    public bool HasFile => _owner.HasFile;

    // ---- Commands ---------------------------------------------------------------------------

    /// <summary>Drop a cut at a normalized track position (parameter is a <see cref="double"/> in [0,1]).</summary>
    public RelayCommand ClickAtCommand { get; }

    /// <summary>Seek to a marker tick's cut (parameter is the <see cref="TimelineTick"/>).</summary>
    public RelayCommand SeekMarkerTickCommand { get; }

    /// <summary>Preview a candidate tick (parameter is the <see cref="TimelineTick"/>).</summary>
    public RelayCommand PreviewCandidateTickCommand { get; }

    // ---- Actions ----------------------------------------------------------------------------

    /// <summary>
    /// Track click → drop a cut at <paramref name="normalizedX"/> (clamped) mapped to a time via
    /// <see cref="TimelineMath.FromNormalized"/>, routed through the owner's
    /// <see cref="SplitViewModel.AddCutAt"/> (which snaps + dedupes). No-op when no file is loaded or
    /// the duration is unknown — there is no meaningful time to cut at.
    /// </summary>
    public void ClickAt(double normalizedX)
    {
        var duration = _owner.Player.Duration;
        if (!_owner.HasFile || duration is not { } d || d <= TimeSpan.Zero)
        {
            return;
        }

        _owner.AddCutAt(TimelineMath.FromNormalized(normalizedX, d));
    }

    /// <summary>Route a marker tick's click to the owner's existing seek-to-marker command.</summary>
    public void SeekMarkerTick(TimelineTick tick)
    {
        if (tick?.Ref is CutMarkerViewModel marker && _owner.SeekToMarkerCommand.CanExecute(marker))
        {
            _owner.SeekToMarkerCommand.Execute(marker);
        }
    }

    /// <summary>Route a candidate tick's click to the owner's existing preview-candidate command.</summary>
    public void PreviewCandidateTick(TimelineTick tick)
    {
        if (tick?.Ref is CandidateViewModel candidate && _owner.PreviewCandidateCommand.CanExecute(candidate))
        {
            _owner.PreviewCandidateCommand.Execute(candidate);
        }
    }

    // ---- Projection -------------------------------------------------------------------------

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Reproject();

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Any playhead move or duration/readiness change reshapes the strip.
        if (e.PropertyName is nameof(PlayerViewModel.Position)
            or nameof(PlayerViewModel.Duration)
            or nameof(PlayerViewModel.IsReady))
        {
            Reproject();
        }
    }

    /// <summary>Recompute the playhead + both tick lists from the current owner state.</summary>
    private void Reproject()
    {
        var duration = _owner.Player.Duration ?? TimeSpan.Zero;

        PlayheadNormalized = TimelineMath.ToNormalized(_owner.Player.Position, duration);

        MarkerTicks = _owner.Markers
            .Select(m => new TimelineTick(
                TimelineMath.ToNormalized(m.Snapped, duration),
                m.Snapped,
                Kind: null,
                Ref: m))
            .ToList();

        CandidateTicks = _owner.Candidates
            .Select(c => new TimelineTick(
                TimelineMath.ToNormalized(c.Candidate.Time, duration),
                c.Candidate.Time,
                c.Candidate.Kind,
                Ref: c))
            .ToList();

        OnPropertyChanged(nameof(HasFile));
    }
}
