using System;
using System.Globalization;
using VideoSplitJoiner.Core.Detect;

namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Wraps one auto-detected <see cref="Candidate"/> for the candidates list: exposes a bindable
/// <see cref="IsSelected"/> checkbox and a compact <see cref="Display"/> (rank, kind, snapped
/// time). The underlying <see cref="Candidate"/> is preserved so its <see cref="Candidate.Time"/>
/// / <see cref="Candidate.SnappedTime"/> can become a marker when "Add selected" runs.
/// </summary>
public sealed class CandidateViewModel : ObservableObject
{
    private bool _isSelected;

    /// <summary>Wrap a detected candidate (initially unselected).</summary>
    public CandidateViewModel(Candidate candidate)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
    }

    /// <summary>The wrapped detector candidate.</summary>
    public Candidate Candidate { get; }

    /// <summary>Whether the user has ticked this candidate for "Add selected".</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>The candidate kind (Black / White / Scene) — bindable for a colour/kind label.</summary>
    public CandidateKind Kind => Candidate.Kind;

    /// <summary>1-based rank across all candidates (1 = highest confidence).</summary>
    public int Rank => Candidate.Rank;

    /// <summary>The keyframe-snapped time this candidate would cut at.</summary>
    public TimeSpan SnappedTime => Candidate.SnappedTime;

    /// <summary>Compact row label, e.g. <c>"#1 Scene  02:14.0  (score 0.87)"</c>.</summary>
    public string Display =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"#{Candidate.Rank} {Candidate.Kind,-5} {CutMarkerViewModel.FormatClock(Candidate.SnappedTime)}  (score {Candidate.Score:0.00})");
}
