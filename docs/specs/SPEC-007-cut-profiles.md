---
id: SPEC-007
slug: cut-profiles
area: core
title: Cut profiles — model, persistence, apply
status: current
sources:
  - src/Core/Profiles/CutProfile.cs
  - src/App/Settings/AppSettings.cs
  - src/App/ViewModels/CutProfileApplier.cs
serves-goal: [G-037]
updated: 2026-08-22
---

## What
A **cut profile** is a reusable, named "keep-the-middle" trim recipe: an absolute intro-end offset measured from the START of a file, plus an optional outro length measured from the END. `CutProfile` (Core, WPF-free, immutable record) is the model with construction-time validation; `AppSettings` persists a list of profiles to `settings.json` (upsert/delete by case-insensitive name, offsets stored as human-readable seconds, tolerant load); `CutProfileApplier` applies a profile to a set of Bulk Cut rows (intro absolute-and-clamped, outro from-end so uneven-length episodes align, each row re-snapping to its own keyframes and re-validating, invalidated rows reported not dropped) and builds a profile from a row's current cut. Storing the outro from the end is what lets ONE profile land correctly on episodes of different lengths.

## Why
Users cutting a season of episodes want to define "trim the 12s intro and the 20s of end credits" ONCE and apply it across files of differing durations. An absolute-from-start intro plus a from-END outro (rather than two absolute times) makes the same profile land correctly on a 22-minute and a 24-minute episode alike (the T-096 apply-to-all convention). The model is deliberately Core-resident and WPF-free so it can be validated, persisted as stable JSON, and unit-tested without an App/UI dependency, and the persistence layer must tolerate corrupt or legacy files without ever crashing the app.

## Scope
**In:** the `CutProfile` record and its validation; `AppSettings` profile persistence (`CutProfiles` list, `SaveProfile` upsert, `DeleteProfile`, JSON round-trip via `CutProfileDto`, tolerant/backward-compatible load); `CutProfileApplier.ApplyProfile` (intro/outro application, per-row re-snap + re-validate, `ApplyToAllReport`) and `CutProfileApplier.BuildProfileFromRow`.
**Out:** the T-103 `BulkCutViewModel` command glue (`SaveProfile`/`ApplyProfileToSelected`/`ApplyProfileToAll`/`DeleteSelectedProfile`, the profile bar, command enable/disable) — a separate app-layer spec; the keyframe-snap and cut-validity engine behind `BulkItemViewModel.IntroEnd`/`OutroStart`/`IsValidCut` (its own spec); the non-profile `AppSettings` fields (folders, layout mode, split ratios).

## Current behavior & invariants

### CutProfile model (`src/Core/Profiles/CutProfile.cs`)
- **I1** — `new CutProfile(name, intro, outro)` with valid values exposes `Name`, `IntroFromStart`, and `OutroFromEnd` unchanged (a present outro is preserved).
- **I2** — the constructor rejects a null / empty / whitespace `Name`, throwing `ArgumentException` (`ValidateName`).
- **I3** — `Name` is stored trimmed of surrounding whitespace (`name.Trim()`), so casing/padding never splits the dedup key.
- **I4** — a negative `IntroFromStart` throws `ArgumentOutOfRangeException` (`ValidateOffset`).
- **I5** — a present but negative `OutroFromEnd` throws `ArgumentOutOfRangeException`; a `null` outro passes validation untouched.
- **I6** — a `null` `OutroFromEnd` is a valid profile meaning "keep runs to EOF, no tail trim".
- **I7** — zero offsets (`TimeSpan.Zero` for intro and/or outro) are accepted (zero is non-negative).
- **I8** — two `CutProfile`s constructed with equal `Name`/`IntroFromStart`/`OutroFromEnd` compare equal (record value semantics).

### Persistence (`src/App/Settings/AppSettings.cs`)
- **I9** — `SaveProfile` upserts by name **case-insensitively, in place**: a save whose name matches an existing profile (any casing) replaces that entry at its current position (`FindIndex` + index assignment), never appending a case-variant duplicate.
- **I10** — `SaveProfile` with a name not already present appends the profile to the end of the list.
- **I11** — `SaveProfile(null)` throws `ArgumentNullException` (`ArgumentNullException.ThrowIfNull`).
- **I12** — saved profiles round-trip through the JSON file: constructing a new `AppSettings` over the same path reloads the identical list in save order (records equal by value); a no-outro profile stays no-outro across the round-trip.
- **I13** — offsets persist as human-readable **seconds** (double) via `CutProfileDto.IntroSeconds`/`OutroSeconds` — never `TimeSpan` ticks (the JSON contains `"introSeconds"`/`"outroSeconds"`).
- **I14** — `DeleteProfile(name)` removes the matching profile **case-insensitively** and persists the removal (survives a reload).
- **I15** — `DeleteProfile` with an unknown name or a blank/whitespace name is a no-op: it throws nothing and leaves the list (and file) unchanged (early-return on blank; `RemoveAll` yields 0 → no `Save`).
- **I16** — backward compatibility: an older `settings.json` that predates the feature (no `cutProfiles` key) loads to an **empty** profile list without crashing and without losing its sibling fields (`lastInputDir`/`lastOutputDir`/`layoutMode`/`horizontalSplitRatio`).
- **I17** — a malformed persisted entry is **skipped** on load, never crashing the load or losing the valid rows: a blank/whitespace name, an offset that fails the finite-non-negative guard (e.g. negative), or any value the `CutProfile` constructor itself rejects (`MapProfiles` `continue`s past it).
- **I18** — duplicate names in the file are deduped on load, case-insensitively, **first occurrence wins** (`HashSet<string>(OrdinalIgnoreCase)` in `MapProfiles`).
- **I19** — when there are no saved profiles, the `cutProfiles` key is **omitted entirely** from the written JSON (the DTO field is set to `null`, not an empty array, so `JsonIgnoreCondition.WhenWritingNull` drops it) — an older/empty file stays byte-clean.

### Apply / build (`src/App/ViewModels/CutProfileApplier.cs`)
- **I20** — `ApplyProfile` sets each ready target's intro-end to `profile.IntroFromStart` **clamped to `[0, Duration]`** (absolute time-from-start), assigned through the `Requested` setter so the row re-snaps to its own keyframes.
- **I21** — when the profile carries an `OutroFromEnd` tail, `ApplyProfile` sets the outro at `Duration − tail` (clamped, measured **FROM END**), so a fixed tail lands at the correct absolute position on episodes of different lengths (e.g. tail 10 → 50 on a 60s file, → 90 on a 100s file).
- **I22** — applying an outro-bearing profile to a row that currently has no outro **adds** an outro at the from-end position (`AddOutro` path).
- **I23** — applying a profile whose `OutroFromEnd` is `null` **clears** the target's existing outro (`ClearOutro`), so the kept span runs to EOF, mirroring the profile's no-outro shape.
- **I24** — a row the applied cut invalidates (intro overshoots, tail longer than the file) is still **counted as applied** (`AppliedCount`) and collected into `ApplyToAllReport.InvalidatedRows` — applied-to and flagged, **never silently dropped**.
- **I25** — rows that are not keyframes-ready or have no probed `Duration` are **skipped** and not counted as applied (the `continue` guard); ready rows in the same batch are still applied.
- **I26** — `ApplyProfile(null profile, …)` and `ApplyProfile(profile, null targets)` each throw `ArgumentNullException`.
- **I27** — `ApplyProfile` returns an `ApplyToAllReport` whose `AppliedCount` equals the number of ready rows applied and whose `InvalidatedRows` lists exactly those applied rows left invalid (empty when all cuts stay valid).
- **I28** — `BuildProfileFromRow(name, row)` captures the inverse of apply: `IntroFromStart` = the row's requested intro-end, and `OutroFromEnd` = `Duration − requested outro-start` when the row has an outro (and a known duration).
- **I29** — `BuildProfileFromRow` on a row **without an outro** produces a profile whose `OutroFromEnd` is `null` (a keep-to-EOF profile).
- **I30** — `BuildProfileFromRow(name, null)` throws `ArgumentNullException`.

## Links
- Design: — (feature tasks T-096 apply-to-all convention · T-102 model/persistence/apply · T-103 VM command glue)
- Goals: G-037
- Related specs: — (T-103 `BulkCutViewModel` profile-commands spec; the keyframe-snap / cut-validity spec — both adjacent, out of scope here)
- Key code: `src/Core/Profiles/CutProfile.cs` · `src/App/Settings/AppSettings.cs` (`CutProfiles`/`SaveProfile`/`DeleteProfile` + `SettingsDto`/`CutProfileDto`) · `src/App/ViewModels/CutProfileApplier.cs`
- Tests: `tests/Core.Tests/CutProfileTests.cs` · `tests/App.Tests/CutProfilePersistenceTests.cs` · `tests/App.Tests/CutProfileApplierTests.cs` (and app-layer `tests/App.Tests/BulkCutProfileCommandsTests.cs`)
