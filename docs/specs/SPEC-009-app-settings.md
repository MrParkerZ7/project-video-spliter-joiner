---
id: SPEC-009
slug: app-settings
area: app
title: App settings persistence
status: current
sources:
  - src/App/Settings/AppSettings.cs
  - src/App/Settings/IAppSettings.cs
serves-goal: [G-010, G-037]
updated: 2026-08-22
---

## What
`AppSettings` is the app's file-backed, cross-session preferences store (T-038). It persists a small
set of "remember where I was / how I had it" values to a single JSON file
(`%APPDATA%/VideoSplitJoiner/settings.json` by default) via `System.Text.Json`: the last input and
output folders, the layout axis, the two per-axis split ratios, and the saved cut profiles. Reads happen
once on construction; every setter persists its change immediately and best-effort. The store is robust
by design — a missing, empty, corrupt, or older/partial file degrades to documented defaults and never
crashes the app, and a write failure is swallowed while the value stays live in memory for the session.

## Why
The app needs preferences that survive a restart (G-010: "remember last input/output location") without
adding a settings dialog, a database, or a crash surface. A single injectable JSON file gives a
human-readable, additive-migration-friendly format: new keys can be added over releases and an older file
that lacks them simply falls back to defaults, never losing the sibling keys it does have. Robustness is a
hard requirement — a settings problem must never take down the app — so every failure mode (no file,
blank, corrupt JSON, out-of-range value, unwritable path, malformed profile row) is defined to degrade
gracefully rather than throw.

## Scope
**In:** the JSON persistence + round-trip contract of `AppSettings`/`IAppSettings` — file location, load
tolerance (missing/blank/corrupt/partial), the folder fields (`LastInputDir`/`LastOutputDir`,
blank→null normalization), the per-axis split ratios (`HorizontalSplitRatio`/`VerticalSplitRatio` and
`ClampRatio` load-side sanitization), `LayoutMode` persistence, the dirty-check setters, the atomic
temp-then-rename write + swallowed-write-failure behavior, and the **settings-persistence** aspects of
`CutProfiles` (round-trip via the file, seconds encoding, missing-field → empty, key-omission, corrupt-row
skip, dedup-on-load, no loss of siblings).

**Out:** the cut-profile *mutation* semantics — `SaveProfile` upsert-in-place and `DeleteProfile`
by-name/no-op, and the profile model's own validation and apply-to-cut behavior — belong to
**SPEC-007 (cut profiles)**. The layout-mode UI/rendering behavior belongs to its own layout spec (D-001);
only the persistence round-trip of `layoutMode` is covered here. `ErrorLogWriter` app-data conventions are
out of scope.

## Current behavior & invariants
- **I1** — `AppSettings.DefaultFilePath()` returns `<ApplicationData>/VideoSplitJoiner/settings.json`
  (folder `AppFolderName = "VideoSplitJoiner"`, file `FileName = "settings.json"`); when
  `Environment.SpecialFolder.ApplicationData` resolves empty it falls back to
  `<Path.GetTempPath()>/VideoSplitJoiner/settings.json`. (`AppSettings.DefaultFilePath`)
- **I2** — The settings file path is injectable: `new AppSettings(string filePath)` uses that path
  verbatim (exposed as `FilePath`) and calls `Load()` during construction, so an existing file's state is
  read immediately on creation. (`AppSettings` ctor, `FilePath`, `Load`)
- **I3** — A null path is rejected: `new AppSettings((string)null!)` throws `ArgumentNullException`.
  (`AppSettings(string)` ctor null guard)
- **I4** — The on-disk JSON is written human-readably indented (`WriteIndented`) with null-valued keys
  omitted entirely (`JsonIgnoreCondition.WhenWritingNull`). (`SerializerOptions`)
- **I5** — Missing file → defaults, no throw: if `_filePath` does not exist, `Load()` returns leaving the
  store at defaults (`LastInputDir`/`LastOutputDir` null, `LayoutMode.Horizontal`, both ratios null,
  empty `CutProfiles`). (`Load`: `!File.Exists`)
- **I6** — Empty or whitespace-only file → defaults, no throw: `Load()` returns early when the file
  contents are `IsNullOrWhiteSpace`. (`Load`)
- **I7** — Corrupt / unparseable JSON → defaults, swallowed, no throw: any exception during read/deserialize
  is caught and the whole store is reset to defaults (nulls, Horizontal, null ratios, empty profiles).
  (`Load` catch block)
- **I8** — Folder round-trip with immediate persist: assigning `LastInputDir` / `LastOutputDir` writes the
  file immediately (setter calls `Save()`), and a fresh `AppSettings` over the same path reloads the same
  string values. (`LastInputDir`/`LastOutputDir` setters + `Load`)
- **I9** — Blank folder normalizes to null on load: a whitespace-only persisted `LastInputDir`/
  `LastOutputDir` reloads as `null` (never set). (`NullIfBlank` applied in `Load`)
- **I10** — Per-axis ratios round-trip independently: `HorizontalSplitRatio` and `VerticalSplitRatio`
  persist to separate JSON keys (`horizontalSplitRatio` / `verticalSplitRatio`) and reload independently
  of each other. (setters + `SettingsDto` keys, D6)
- **I11** — Never-set ratio defaults to null: a ratio that was never assigned reloads as `null`, meaning
  "use the layout default." (default field state)
- **I12** — Out-of-range finite ratio clamped on load: a persisted numeric ratio outside `[0.05, 0.95]` is
  clamped into that band on load (via `Math.Clamp(v, 0.05, 0.95)`) so a corrupt value can never wedge a
  pane to zero. (`ClampRatio`)
- **I13** — Non-finite ratio → null on load: a persisted `NaN` or `Infinity` (and a null) ratio maps to
  `null` (use the default), not a clamped number. (`ClampRatio` NaN/Infinity guard)
- **I14** — `LayoutMode` persistence: `LayoutMode` round-trips as a stable string (`"Horizontal"`/
  `"Vertical"`, written via `_layoutMode.ToString()`); a missing key or any unknown/typo string parses
  case-insensitively to the `LayoutMode.Horizontal` default with no throw. (`ParseLayoutMode`, `Save`)
- **I15** — Dirty-check setters skip redundant writes: assigning a setter its current value does NOT
  re-persist — `LastInputDir`/`LastOutputDir` compare with `StringComparison.Ordinal`, the ratios with
  `Nullable.Equals`, `LayoutMode` with `!=`; only a genuine change calls `Save()`. (all setter guards)
- **I16** — Atomic write via temp-then-rename: `Save()` writes to `<path>.tmp` (UTF-8, no BOM) then
  `File.Replace` when the target exists / `File.Move` when it does not, so a crash mid-write can never
  replace a good file with a half-written one; a stray `.tmp` is cleaned up on failure. (`Save`,
  `TryDeleteTemp`)
- **I17** — Write failure swallowed, value kept in memory: when the directory is uncreatable / the file is
  locked, `Save()` catches the failure, nothing is written to the target path, and the assigned value
  stays live in memory for the session; the setter never throws to the caller. (`Save` catch + `TryDeleteTemp`)
- **I18** — Cut profiles round-trip through the file: saved profiles persist and reload in save order, with
  offsets stored as human-readable **seconds** (double: `introSeconds` / `outroSeconds`), never TimeSpan
  ticks; a null outro stays null across the round-trip. (`Save` `TotalSeconds` mapping + `MapProfiles`)
- **I19** — Missing `cutProfiles` field → empty list, siblings intact: an older file that omits the
  `cutProfiles` key loads to an empty profile list without crashing and without losing the sibling folder,
  layout, and ratio fields (additive migration never drops present siblings). (`MapProfiles` null→empty +
  independent per-field mapping in `Load`)
- **I20** — Empty profile set omits the key: when there are no saved profiles, `Save()` writes the
  `cutProfiles` slot as null so the key is omitted entirely (empty list ≠ an empty array on disk),
  keeping older/empty files byte-clean. (`Save` `Count == 0 ? null` + `WhenWritingNull`)
- **I21** — Corrupt profile row skipped on load: a persisted profile entry with a blank name, a
  non-finite/negative `introSeconds`/`outroSeconds`, or one the `CutProfile` record's own validation
  rejects is skipped rather than crashing the load; valid sibling rows survive. (`MapProfiles`,
  `IsFiniteNonNegative`, guarded `new CutProfile(...)`)
- **I22** — Duplicate profile names deduped on load: duplicate names in the file collapse
  case-insensitively with first-occurrence-wins. (`MapProfiles` `seen` HashSet, `StringComparer.OrdinalIgnoreCase`)

## Links
- Design: D-001 (vertical-monitor layout — persisted layout state); ADR-0016 (cut profiles)
- Goals: G-010 (remember last input/output location), G-037 (reusable cut profiles)
- Related specs: SPEC-007 (cut profiles — profile model, upsert/delete/apply semantics)
- Key code: src/App/Settings/AppSettings.cs, src/App/Settings/IAppSettings.cs, src/Core/Profiles/CutProfile.cs
