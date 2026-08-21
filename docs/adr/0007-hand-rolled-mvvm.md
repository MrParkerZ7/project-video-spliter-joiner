# ADR 0007: Hand-rolled MVVM — no CommunityToolkit.Mvvm, view models WPF-free for headless test

## Status

Accepted.

## Context

VideoSplitJoiner is a two-project .NET 8 solution: `src/Core`
(`VideoSplitJoiner.Core.csproj`, plain `net8.0`, no WPF, `TreatWarningsAsErrors`)
holds the ffmpeg-backed split/join/probe engines, and `src/App`
(`VideoSplitJoiner.App.csproj`, `net8.0-windows`, `UseWPF=true`) holds the WPF
shell and the view models under `src/App/ViewModels/`. The engines are already
kept UI-free and independently testable — see
[0009](0009-two-path-keyframe-scan.md) for the Core-side split/scan work that the
view models drive.

The view-model layer needed a `INotifyPropertyChanged` base and an `ICommand`
implementation. The idiomatic move is to pull in **CommunityToolkit.Mvvm** for its
`ObservableObject`, `[ObservableProperty]` source generators, and `RelayCommand`.
Two forces argued against it here:

- **Restore surface.** The app already ships a bundled ffmpeg (see
  [0010](0010-shared-ffmpeg-bundling.md)) and a single-file self-contained publish
  ([0011](0011-single-file-publish-no-trim.md)); the only App-side NuGet dependency
  is `FFME.Windows` (`src/App/VideoSplitJoiner.App.csproj`). Adding an MVVM
  framework buys a second package + source generators for a base class that is
  ~35 lines. `src/App/ViewModels/ObservableObject.cs` says so in its own header:
  *"Kept dependency-free on purpose (no CommunityToolkit.Mvvm) to avoid a restore
  dependency."*
- **Headless testability.** The view models are the composition root — `MainViewModel`
  hand-wires the real Core graph (`FfmpegBinaryLocator → runners → MediaProbe →
  Split/JoinEngine`) in its parameterless ctor. To unit-test that logic without a
  window, a `MediaElement`, or real playback, each VM must be constructible with
  fakes and must not touch WPF rendering. `SplitViewModel`'s header states the rule
  directly: *"Deliberately WPF-free and constructor-injected so it is fully
  unit-testable with fakes."*

## Decision

**Hand-roll the MVVM primitives and keep the view models WPF-rendering-free and
constructor-injected.** Concretely:

- `ObservableObject.cs` — a minimal `INotifyPropertyChanged` base with a generic
  `SetProperty<T>(ref field, value, [CallerMemberName])` helper. No source
  generators, no package.
- `RelayCommand.cs` — a minimal `ICommand` (delegate-relay, with a
  parameterless-and-parameterized ctor pair). This is the one deliberate WPF touch
  point: it uses `System.Windows.Input.ICommand` / `CommandManager` (the framework
  contract every WPF binding needs), which is why the App test project targets
  `net8.0-windows` with `UseWPF=true` rather than plain `net8.0`.
- **All rendering / device dependencies sit behind interfaces**, injected via ctor:
  the preview player behind `IMediaPlayer` (`FfmeMediaPlayer` in production,
  `NullMediaPlayer` / a per-test `FakeMediaPlayer` in tests), thumbnails behind the
  service consumed by `NullThumbnailService`, settings behind `IAppSettings`, and
  the Core engines behind `IMediaProbe` / `ISplitEngine` / `ISplitEngine`'s join
  counterpart. Each VM exposes **two constructors**: a parameterless/production one
  that builds the real graph, and a DI-style one that accepts already-composed
  collaborators (`MainViewModel(SplitViewModel, JoinViewModel?)`,
  `SplitViewModel(probe, engine, player, settings, thumbnailService)`).
- **Two enforcement guards:**
  - `CoreIsUiFreeTests` (`tests/Core.Tests/CoreIsUiFreeTests.cs`) fails the build if
    `VideoSplitJoiner.Core` ever references `PresentationFramework`,
    `PresentationCore`, or `WindowsBase` — keeping the UI⇄Core seam clean at the
    assembly level.
  - The **ctor-injected VMs themselves** are the App-side guard: because every
    rendering dependency is an injected interface, the `App.Tests` suite
    (`PlayerViewModelTests`, `SplitViewModelTests`, `JoinViewModelTests`, …)
    constructs each VM with fakes and exercises observable state, command guards,
    title composition (`MainViewModel.ComposeWindowTitle`, a `static` pure method),
    and the seek-feedback logic with *no window, no `MediaElement`, no real
    playback*.

## Consequences

**Positive**

- One fewer NuGet dependency and zero MVVM source generators — smaller restore,
  smaller single-file publish, nothing to keep in ABI-lockstep.
- View-model logic is deterministically unit-testable headlessly; the fakes record
  transport calls and raise player events (`DurationAvailable` / `PositionChanged` /
  `Ended` / `Failed`) so tests drive VM state directly.
- Reinforces the Core purity boundary asserted by `CoreIsUiFreeTests` — the WPF
  surface is confined to `src/App`, and even there the VMs only touch the `ICommand`
  contract, not rendering.
- The base primitives are tiny and fully readable — no generator magic to reason
  about when a binding misbehaves.

**Negative**

- No `[ObservableProperty]` / `[RelayCommand]` source generators: every bindable
  property is a hand-written `get`/`SetProperty(ref …)` pair and every command is an
  explicit `RelayCommand` field — more boilerplate than the toolkit's attributes.
- The primitives are ours to maintain; any future need (async relay command with
  built-in `IsRunning`, `ObservableRecipient`-style messaging) is a hand-written
  addition rather than a package feature.
- "WPF-free" is precise, not absolute: `RelayCommand` binds to
  `System.Windows.Input`, so the VM assembly and its test project are
  `net8.0-windows`. The guarantee is *no rendering / device dependency and no live
  playback in tests* — not *zero WPF types*.

**Follow-ons**

- New view models MUST follow the two-ctor pattern (production graph + injected
  fakes) and route every device/rendering dependency through an interface —
  otherwise the headless-test guarantee silently erodes (there is no automated
  App-side equivalent of `CoreIsUiFreeTests` catching a VM that news-up a
  `MediaElement`; the discipline is the ctor shape).
- If the boilerplate cost ever outweighs the restore/testability win (e.g. the VM
  count grows sharply), revisit adopting CommunityToolkit.Mvvm — but only if its
  `RelayCommand`/`ObservableObject` can be introduced without breaking the
  ctor-injection + WPF-rendering-free contract this ADR protects.
