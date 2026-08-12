using System;
using System.Collections.Generic;
using FluentAssertions;
using VideoSplitJoiner.App.ViewModels;
using Xunit;

namespace VideoSplitJoiner.App.Tests;

/// <summary>
/// T-084 / D-002 — pure unit tests for the WPF-free <see cref="WaveformViewModel"/>: the DATA + STATE
/// the <c>TimelineView</c> code-behind binds to (Peaks / HasAudio / IsLoading). No WPF, no ffmpeg — the
/// state machine (BeginLoad → ApplyPeaks / ApplyNoAudio → Reset) is asserted directly, including the
/// defensive copy so a later mutation of the caller's array can't corrupt a shown wave, and the
/// PropertyChanged notifications the view redraws on.
/// </summary>
public sealed class WaveformViewModelTests
{
    [Fact]
    public void FreshVm_IsEmpty_NoAudio_NotLoading()
    {
        var vm = new WaveformViewModel();

        vm.Peaks.Should().BeEmpty();
        vm.HasAudio.Should().BeFalse();
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public void BeginLoad_EntersLoading_HidesBand_DropsStalePeaks()
    {
        var vm = new WaveformViewModel();
        vm.ApplyPeaks(new[] { 0.5f, 0.9f }); // a prior file's wave is showing
        vm.HasAudio.Should().BeTrue();

        vm.BeginLoad();

        vm.IsLoading.Should().BeTrue();
        vm.HasAudio.Should().BeFalse("no stale wave is shown against the new file while it extracts");
        vm.Peaks.Should().BeEmpty();
    }

    [Fact]
    public void ApplyPeaks_NonNull_ShowsBand_StoresPeaks_ClearsLoading()
    {
        var vm = new WaveformViewModel();
        vm.BeginLoad();

        var peaks = new[] { 0.1f, 0.5f, 1f, 0.2f };
        vm.ApplyPeaks(peaks);

        vm.HasAudio.Should().BeTrue();
        vm.Peaks.Should().Equal(peaks);
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public void ApplyPeaks_Null_HidesBand_ClearsLoading()
    {
        var vm = new WaveformViewModel();
        vm.BeginLoad();

        vm.ApplyPeaks(null);

        vm.HasAudio.Should().BeFalse();
        vm.Peaks.Should().BeEmpty();
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public void ApplyNoAudio_IsEquivalentToNullPeaks()
    {
        var vm = new WaveformViewModel();
        vm.BeginLoad();

        vm.ApplyNoAudio();

        vm.HasAudio.Should().BeFalse();
        vm.Peaks.Should().BeEmpty();
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public void ApplyPeaks_StoresDefensiveCopy_CallerMutationDoesNotCorruptWave()
    {
        var vm = new WaveformViewModel();
        var peaks = new[] { 0.2f, 0.4f, 0.6f };

        vm.ApplyPeaks(peaks);
        peaks[0] = 9.9f; // mutate the caller's array AFTER applying

        vm.Peaks.Should().Equal(new[] { 0.2f, 0.4f, 0.6f }, "the VM stored its own copy");
    }

    [Fact]
    public void Reset_ClearsPeaks_HidesBand_ClearsLoading()
    {
        var vm = new WaveformViewModel();
        vm.ApplyPeaks(new[] { 0.3f, 0.7f });
        vm.HasAudio.Should().BeTrue();

        vm.Reset();

        vm.Peaks.Should().BeEmpty();
        vm.HasAudio.Should().BeFalse();
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public void ApplyPeaks_RaisesPropertyChanged_ForPeaksHasAudioAndLoading()
    {
        var vm = new WaveformViewModel();
        vm.BeginLoad();

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ApplyPeaks(new[] { 0.5f });

        changed.Should().Contain(nameof(WaveformViewModel.IsLoading));
        changed.Should().Contain(nameof(WaveformViewModel.Peaks));
        changed.Should().Contain(nameof(WaveformViewModel.HasAudio));
    }

    [Fact]
    public void HasAudio_ToggleShownThenHidden_RaisesChangeEachTime()
    {
        var vm = new WaveformViewModel();

        var hasAudioChanges = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WaveformViewModel.HasAudio))
            {
                hasAudioChanges++;
            }
        };

        vm.ApplyPeaks(new[] { 0.5f }); // false → true
        vm.ApplyNoAudio();             // true → false

        hasAudioChanges.Should().Be(2, "the view redraws/collapses on each HasAudio flip");
    }
}
