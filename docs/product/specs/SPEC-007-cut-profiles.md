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
  - src/App/Settings/ProfileThumbnailStore.cs
  - src/App/Settings/ProfileBackup.cs
  - packaging/VideoSplitJoiner.iss
  - src/App/ViewModels/BulkCutViewModel.cs
  - src/App/ImageSignature.cs
  - src/App/Io/ImageNormalizer.cs
  - src/App/Views/BulkCutView.xaml
  - src/App/Views/Converters.cs
  - src/App/Views/ProfilePreviewCard.cs
serves-goal: [G-037, G-038, G-044, G-051, G-053, G-054, G-057, G-056, G-058]
updated: 2026-09-25
---

## What
A **cut profile** is a reusable, named "keep-the-middle" trim recipe: an absolute intro-end offset measured from the START of a file, plus an optional outro length measured from the END. `CutProfile` (Core, WPF-free, immutable record) is the model with construction-time validation; `AppSettings` persists a list of profiles to `settings.json` (upsert/delete by case-insensitive name, offsets stored as human-readable seconds, tolerant load); `CutProfileApplier` applies a profile to a set of Bulk Cut rows (intro absolute-and-clamped, outro from-end so uneven-length episodes align, each row re-snapping to its own keyframes and re-validating, invalidated rows reported not dropped) and builds a profile from a row's current cut. Storing the outro from the end is what lets ONE profile land correctly on episodes of different lengths.

A profile also carries an **optional thumbnail** (G-038 / T-106): `CutProfile.ThumbnailPath` is an optional PATH string (never image bytes) that survives the JSON round-trip backward-compatibly; `ProfileThumbnailStore` copies a chosen frame/image into a per-user `profile-thumbs` folder under a deterministic, collision-resistant safe name and best-effort removes it, and `AppSettings.DeleteProfile` cascades that removal. The T-107 view-model glue on `BulkCutViewModel` auto-captures the row's intro-end frame as the profile's default thumbnail on save (best-effort — a failed grab never blocks the save), with upload-override and clear.

The two thumbnail paths deliberately have **different contracts** (T-129 / G-044). The **auto** capture on save is a side effect of "Save" and stays silent: it must never interrupt the save, so a failed grab or a store refusal simply leaves the profile without a thumbnail. The **explicit upload** is a deliberate user gesture ("I picked this file") and **reports**: a failure leaves the current thumbnail untouched, as before, but now surfaces a headline + actionable hint + copyable detail on the screen's existing error block (`OperationViewModel.Error`, via the additive `OperationViewModel.ReportFailure`) instead of being swallowed — a silent upload is indistinguishable from a broken button.

Profiles are **durable and portable** (T-147 / G-051). Durable: the installer removes nothing under the
user profile, so uninstalling and reinstalling leaves every profile and picture in place - asserted
directly against the real `.iss` file rather than left as an accident of the current script. Portable:
`ProfileBackup` writes every profile to ONE self-contained file with its picture inline as base64, and
reads one back as a plan-then-apply upsert. Inline images exist because a profile lives across **two
roots** - the profile in Roaming `%APPDATA%`, its picture in Local `%LOCALAPPDATA%` - so anything that
carries only the settings file keeps the profiles and silently loses every picture (ADR-0021). Import is
deliberately incapable of quietly costing someone what they already had: a corrupt or future-version file
fails at the planning stage and changes nothing, and a name collision is resolved by the caller, whose
default answer is to keep the existing profile.

## Why
Users cutting a season of episodes want to define "trim the 12s intro and the 20s of end credits" ONCE and apply it across files of differing durations. An absolute-from-start intro plus a from-END outro (rather than two absolute times) makes the same profile land correctly on a 22-minute and a 24-minute episode alike (the T-096 apply-to-all convention). The model is deliberately Core-resident and WPF-free so it can be validated, persisted as stable JSON, and unit-tested without an App/UI dependency, and the persistence layer must tolerate corrupt or legacy files without ever crashing the app.

## Scope
**In:** the `CutProfile` record and its validation (including the optional `ThumbnailPath` member); `AppSettings` profile persistence (`CutProfiles` list, `SaveProfile` upsert, `DeleteProfile` + its thumbnail-file cascade, JSON round-trip via `CutProfileDto` incl. `thumbnailPath`, tolerant/backward-compatible load); `CutProfileApplier.ApplyProfile` (intro/outro application, per-row re-snap + re-validate, `ApplyToAllReport`) and `CutProfileApplier.BuildProfileFromRow`; the `ProfileThumbnailStore` file store (`Save`/`Delete`/`DeleteByPath`/`DefaultRoot`/`SafeFileName`); and the **T-107 thumbnail glue** on `BulkCutViewModel` (`SaveProfileWithAutoThumbnailAsync` auto-default capture, `UploadThumbnail`, `ClearThumbnail`, `AttachThumbnail`/`TryAttachThumbnail`) **plus the T-129 upload-failure reporting** on that glue (`ThumbnailAttachOutcome`, `ReportThumbnailUploadFailure`, `ClearThumbnailUploadError`, and the messages they place on `Operation.Error`).
Also in (T-147): `ProfileBackup` (`Export`, `Plan`, `Apply`, `ImportPlan`, the versioned file shape) and the `BulkCutViewModel` glue over it (`ExportProfiles`/`ImportProfiles`, `ExportProfilesCommand`/`ImportProfilesCommand`, the `ChooseProfileExportPath`/`ChooseProfileImportPath`/`ConfirmProfileOverwrite` host hooks), plus the **installer's hands-off guarantee** over user-data folders.
Also in (T-161/T-168/T-169/T-170): the `ProfileBar` chip picker in `BulkCutView.xaml` and its hover preview card (I95–I99, I101–I105), the upload's not-an-image refusal (`ImageSignature`, I100), and upload width normalization (`ImageNormalizer`, I106–I107).
**Out:** the `OperationViewModel` lifecycle itself — state machine, progress, ETA, taskbar mapping, and the `ReportFailure` entry point's own state rules (SPEC-008); the WPF error block that renders `Operation.Error` (SPEC-011/SPEC-015); the **non-thumbnail** T-103 `BulkCutViewModel` command glue (`SaveProfile`/`ApplyProfileToSelected`/`ApplyProfileToAll`/`DeleteSelectedProfile`, the profile bar's card layout and control gating, command enable/disable) — covered by SPEC-011; the keyframe-snap and cut-validity engine behind `BulkItemViewModel.IntroEnd`/`OutroStart`/`IsValidCut` (its own spec); the per-row cut-point frame thumbnails (T-108 — SPEC-011); the non-profile `AppSettings` fields (folders, layout mode, split ratios); `PathToBitmapConverter`'s own path→bitmap rendering; the file dialogs and MessageBox behind the backup hooks (view glue); automatic/scheduled/cloud backup (not built - backup is a manual gesture); and any migration of existing installs between the two storage roots (explicitly rejected - ADR-0021).

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
- **I20** — `ApplyProfile` sets the intro-end of each target with a probed `Duration` (`CanTakeCut` — a target whose keyframe scan is still running included, T-173) to `profile.IntroFromStart` **clamped to `[0, Duration]`** (absolute time-from-start), assigned through the `Requested` setter so the row re-snaps to its own keyframes — at once, or when its scan lands.
- **I21** — when the profile carries an `OutroFromEnd` tail, `ApplyProfile` sets the outro at `Duration − tail` (clamped, measured **FROM END**), so a fixed tail lands at the correct absolute position on episodes of different lengths (e.g. tail 10 → 50 on a 60s file, → 90 on a 100s file).
- **I22** — applying an outro-bearing profile to a row that currently has no outro **adds** an outro at the from-end position (`AddOutro` path).
- **I23** — applying a profile whose `OutroFromEnd` is `null` **clears** the target's existing outro (`ClearOutro`), so the kept span runs to EOF, mirroring the profile's no-outro shape.
- **I24** — a row the applied cut invalidates (intro overshoots, tail longer than the file) is still **counted as applied** (`AppliedCount`) and collected into `ApplyToAllReport.InvalidatedRows` — applied-to and flagged, **never silently dropped**. For a target still scanning keyframes that verdict is given only when no snap can rescue the cut (I108), and the row is also counted in `InvalidStillScanningCount`; any other scanning target is not invalid and goes to `PendingSnapRows`.
- **I25** — rows with no probed `Duration` (not probed yet, or the probe failed) are **skipped**: untouched, not counted as applied, and counted in `ApplyToAllReport.SkippedNotLoadedCount` (the `!CanTakeCut` guard). A row still scanning keyframes is **not** skipped (T-173): it is applied, and its handles stay snap-pending until the scan lands.
- **I26** — `ApplyProfile(null profile, …)` and `ApplyProfile(profile, null targets)` each throw `ArgumentNullException`.
- **I27** — `ApplyProfile` returns an `ApplyToAllReport` whose `AppliedCount` equals the number of targets with a probed `Duration` applied; whose `InvalidatedRows` lists exactly the applied rows classified invalid by I108 (empty when every cut stays valid), `InvalidStillScanningCount` of them still scanning; whose `PendingSnapRows` lists the applied rows waiting for their scan, disjoint from `InvalidatedRows`; and whose `SkippedNotLoadedCount` counts the targets without a `Duration`.
- **I28** — `BuildProfileFromRow(name, row)` captures the inverse of apply: `IntroFromStart` = the row's requested intro-end, and `OutroFromEnd` = `Duration − requested outro-start` when the row has an outro (and a known duration).
- **I29** — `BuildProfileFromRow` on a row **without an outro** produces a profile whose `OutroFromEnd` is `null` (a keep-to-EOF profile).
- **I30** — `BuildProfileFromRow(name, null)` throws `ArgumentNullException`.

### CutProfile thumbnail — model (T-106) (`src/Core/Profiles/CutProfile.cs`)
- **I31** — `CutProfile` takes an optional fourth `ThumbnailPath` parameter; a profile constructed with a non-blank path exposes it unchanged (`ThumbnailPath`).
- **I32** — the thumbnail is optional: constructing without the argument (the 3-arg form) yields `ThumbnailPath == null` (absent ⇒ no thumbnail).
- **I33** — a `null` / empty / whitespace `ThumbnailPath` normalizes to `null` (`NormalizeThumbnailPath`), so an empty string never masquerades as a real thumbnail.
- **I34** — a non-blank `ThumbnailPath` is stored **trimmed** of surrounding whitespace (like `Name`).
- **I35** — `ThumbnailPath` is plain metadata: the record performs **no** existence/format validation on it (unlike the range-checked offsets), so a nonexistent or non-image path constructs without throwing.
- **I36** — a record `with { ThumbnailPath = … }` sets the thumbnail while leaving the other fields intact and the original instance unchanged (the copy-on-attach path T-107 uses).
- **I37** — `ThumbnailPath` participates in record value-equality: two profiles equal on `Name`/`IntroFromStart`/`OutroFromEnd` but differing only on `ThumbnailPath` are **not** equal.

### Thumbnail persistence — round-trip (T-106) (`src/App/Settings/AppSettings.cs`)
- **I38** — `ThumbnailPath` round-trips through the JSON file: a profile saved with a thumbnail path reloads (new `AppSettings` over the same path) with the identical path, and a no-thumbnail profile stays `null` across the round-trip (`CutProfileDto.ThumbnailPath`, `MapProfiles`).
- **I39** — the thumbnail persists as a PATH **string** (the JSON carries a `"thumbnailPath"` key holding the path), never image bytes (`CutProfileDto.ThumbnailPath` is `string?`).
- **I40** — a `null` `ThumbnailPath` is **omitted entirely** from the written JSON (`JsonIgnoreCondition.WhenWritingNull`), so a no-thumbnail profile stays byte-clean (same discipline as I19).
- **I41** — backward compatibility: an older `cutProfiles` entry that predates the field (no `thumbnailPath` key) loads with `ThumbnailPath == null` and its sibling fields (name/offsets) intact — an additive, non-breaking migration (`MapProfiles` → `NullIfBlank(dto.ThumbnailPath)`).

### Thumbnail file store — `ProfileThumbnailStore` (T-106) (`src/App/Settings/ProfileThumbnailStore.cs`)
- **I42** — `Save(profileName, sourcePath)` copies the source file into the store root (bytes verbatim), creates the root on demand, and returns the stored **absolute** path — the value assigned to `CutProfile.ThumbnailPath` (`Save`, `Directory.CreateDirectory`).
- **I43** — `Save` preserves a **recognized** source image extension (lowercased) on the stored file (`NormalizeExtension`, `KnownImageExtensions`).
- **I44** — `Save` with an **unrecognized** source extension defaults the stored file to `.png` (`DefaultExtension`).
- **I45** — `Save` displaces the profile's prior thumbnail — including one stored under a **different extension** — so exactly one thumbnail file per profile survives a save. It does **not** delete first: the order is (1) copy the source to a `<safe>.incoming<ext>` **staging** file in the root, (2) rename every prior thumbnail carrying that safe stem **aside** to a `<file>.vsj-aside` sibling — this is the cross-extension sweep, which the destination path alone could never cover, (3) `File.Move(staging → destination, overwrite: true)`, (4) delete the asides. The cross-extension guarantee this invariant is really about is unchanged; what changed is *when* the old file goes — now only after the new bytes are safely on disk, which is what makes a failed `Save` non-destructive (I73) (`Save`, `RenameExistingAside`, `NormalizeExtension`).
- **I46** — `Save` **sanitizes** an odd profile name (invalid filename chars → `_`) into a safe filename, so a name with illegal characters copies successfully instead of raising an I/O error (`SafeFileName`).
- **I47** — `Save` throws `ArgumentException` (`profileName`) on a blank/whitespace profile name.
- **I48** — `Save` throws `ArgumentException` (`sourceImageOrFramePath`) on a blank/whitespace source path.
- **I49** — `Save` throws `FileNotFoundException` when the source file does not exist — a genuine caller error, distinct from the best-effort deletes (`Save` guard).
- **I50** — `Delete(profileName)` removes the profile's stored thumbnail file (`Delete` → `DeleteExistingFor`).
- **I51** — `Delete` resolves the file **case-insensitively**, matching the profile upsert key (`Delete("series")` removes the file saved under `"Series"`).
- **I52** — `Delete` is best-effort: an unknown / never-saved / blank / `null` name is a no-op that **never throws** (`Delete` early-return, `TryDeleteFile` swallow).
- **I53** — `DeleteByPath(path)` best-effort removes a specific stored file by its path (`DeleteByPath` → `TryDeleteFile`).
- **I54** — `DeleteByPath` is best-effort on a missing / blank / `null` path — a no-op that never throws.
- **I55** — `DefaultRoot()` resolves to `%LOCALAPPDATA%/VideoSplitJoiner/profile-thumbs` (mirroring the thumb-cache composition), with an OS-temp fallback when local-app-data cannot be resolved (`DefaultRoot`, `AppFolderName`, `ThumbsFolderName`).
- **I56** — `SafeFileName` is **collision-resistant**: two distinct names that sanitize to the same readable stem still map to different files, via a short SHA-256-derived hash suffix (`SafeFileName`, `ShortHash`).
- **I57** — `SafeFileName` is **case-insensitively stable** (same file for `Foo`/`foo`), matching the upsert key, so `Save`/`Delete`/the cascade all resolve the same file across sessions.
- **I58** — the store's root is **injectable** and construction is side-effect-free — no directory is created until the first `Save` (`ProfileThumbnailStore(string root)` ctor; every test redirects the root away from the real per-user folder).

### DeleteProfile → thumbnail cascade (T-106) (`src/App/Settings/AppSettings.cs`)
- **I59** — `DeleteProfile` **cascades** to the thumbnail file: after removing the profile it best-effort deletes the stored thumbnail both by the recomputed safe name (`store.Delete(name)`) **and** by the exact recorded path (`store.DeleteByPath(removedProfile.ThumbnailPath)`), covering a directly-set path that diverges from the safe-name path (`DeleteProfile`).
- **I60** — the cascade is optional and best-effort: an `AppSettings` with **no** wired store (`AppSettings(file)`) simply skips the thumbnail cleanup — the profile is still removed and persisted, without throwing (`_thumbnailStore?.Delete`).

### Profile-thumbnail glue — auto-default / upload / clear (T-107) (`src/App/ViewModels/BulkCutViewModel.cs`)
- **I61** — `SaveProfileWithAutoThumbnailAsync(name)` auto-captures the selected row's **intro-end frame** as the profile's default thumbnail: it grabs exactly one frame at `row.IntroEnd.Snapped` (width `ProfileThumbnailWidth` = 640 — the one stored width of I106), copies it into the `ProfileThumbnailStore`, persists the stored path onto the profile, and re-points the bar's `SelectedProfile` at the thumbnailed instance.
- **I62** — the auto-default persists the profile **first** and is never blocked on the grab: a grab that returns **null** still saves the profile with a `null` thumbnail (placeholder) (`SaveProfileWithAutoThumbnailAsync` step 1 → `SaveProfile`, then best-effort attach).
- **I63** — a grab that **throws** never blocks or fails the save: the profile still saves with a `null` thumbnail (`SaveProfileWithAutoThumbnailAsync` try/catch, and the `TryAttachThumbnail` catch behind `AttachThumbnail`).
- **I64** — with **no selected row**, `SaveProfileWithAutoThumbnailAsync` is a no-op: nothing is saved and no frame grab is attempted.
- **I65** — `UploadThumbnail(profile, imagePath)` overrides the thumbnail: it copies the chosen image into the store (extension preserved), persists the stored path onto the profile, and re-points the bar selection (`UploadThumbnail` → `AttachThumbnail`).
- **I66** — **the AUTO capture path is silent** (re-scoped by T-129 — it used to cover the upload too): `AttachThumbnail`, the wrapper `SaveProfileWithAutoThumbnailAsync` uses, discards the attach outcome, so an un-persisted profile or a store refusal leaves the profile's current thumbnail untouched, reports **nothing**, and never throws (`AttachThumbnail` → `TryAttachThumbnail`, outcome discarded). Best-effort is the right contract here and only here: the thumbnail is a side effect of "Save" and must never interrupt the save.
- **I67** — `ClearThumbnail(profile)` nulls the profile's `ThumbnailPath` **and** best-effort deletes the stored file(s) — by name and by the exact recorded path — then re-points the bar selection, so the picker reverts to the placeholder; it is a no-op when the profile is unset or has no persisted entry (`ClearThumbnail`).

#### Explicit upload reports its failures (T-129 / G-044) (`src/App/ViewModels/BulkCutViewModel.cs`)
- **I68** — `UploadThumbnail` returns `true` **only** when the image was actually attached, `false` on every failure, and still **never throws**. A failure still leaves the profile's current thumbnail untouched — the no-op half of the old I66 is preserved verbatim; only the *silence* is dropped (`UploadThumbnail` → `TryAttachThumbnail`).
- **I69** — every explicit-upload failure is **reported to the user** on the screen's existing error surface (`Operation.Error`, set through the additive `OperationViewModel.ReportFailure` — no dialog, no new surface), each with its own headline and an actionable `Hint`. The copyable `RawTail` is **assembled from the parts that exist**, each appended only when non-blank and newline-joined: the chosen path, then the refusing exception's message (`ReportThumbnailUploadFailure` → `parts`). It therefore carries **both** only for the two outcomes where a store call actually threw (`ImageUnreadable` / `StoreFailed`); `NoProfile` and `ProfileNotSaved` carry the **path alone**, because those are decided without the store ever throwing (`detail` stays empty); and `NoImageChosen` carries **nothing at all** — that branch is *defined* by a blank/whitespace path and raises no exception, so its `RawTail` is the empty string and the headline plus `Hint` are the whole message. The five reportable outcomes are distinct (`ThumbnailAttachOutcome`): `NoProfile` (nothing selected), `NoImageChosen` (null/blank path), `ProfileNotSaved` (the name has no persisted profile), `ImageUnreadable` (the store refused the source — `FileNotFoundException`/`DirectoryNotFoundException`/`ArgumentException`), `StoreFailed` (any other store failure — unwritable root, locked target, I/O **or access** error — see I73 on the exception type).
- **I70** — a failed upload does **no extra work**: it returns before the profile upsert, so there is no `SaveProfile` write and no `RefreshProfiles` re-projection (the bar's `SelectedProfile` stays the SAME instance, not a re-created record), and no frame grab is attempted (the explicit path never touches `IThumbnailService`). The only I/O is the single `ProfileThumbnailStore.Save` call that refused, so the profile's recorded `ThumbnailPath` and the file it points at both survive **any** refusal — not only the early ones that never reach the disk (blank/missing source — I49's guard; unwritable root — the `Directory.CreateDirectory` step) but a late one too, because the store is copy-then-swap (I73).
- **I71** — a later **successful** upload retracts the message it reported (`Operation.Error` back to `null`, and the state out of `Failed`), and retracts **only that message**: the retraction is reference-scoped (`ClearThumbnailUploadError`), so an unrelated batch failure sitting on the same surface (e.g. a Blocked disk pre-flight) survives a successful thumbnail upload.
- **I72** — the auto path never reports (the other half of I66, asserted from the outside): a `SaveProfileWithAutoThumbnailAsync` whose store attach fails still saves the profile, leaves it with a `null` thumbnail, and leaves `Operation.Error` **null** — changing the upload contract did not leak into the save.

#### A failed `Save` never destroys the thumbnail it was replacing (G-044)
- **I73** — the store is **copy-then-swap**, so a `Save` that fails leaves the profile's prior thumbnail **byte-identical** and leaves no stray working file behind. This is the fix for what SPEC-007 used to record as a live "known store-side gap" under I70: under the old delete-before-copy order, a copy that failed *after* the delete (a source locked by another program, a full or read-only volume) destroyed the picture the profile already had — the caller correctly reported `StoreFailed` and correctly left `CutProfile.ThumbnailPath` untouched, but the path then pointed at a file that no longer existed and the picker silently reverted to the placeholder. Both failure points are now covered: a **copy** that fails happens before any prior file has been touched and sweeps its own partial staging file; a **move** that fails restores the asides over their originals and then sweeps the staging file — so the half-swapped state is never observable to the caller. Both cleanups are best-effort (`TryDeleteFile` / `RestoreAsides` swallow), which is a strictly safer failure mode, not a hole: in the pathological case where the rename-back itself fails the prior bytes still exist under the `.vsj-aside` sibling rather than being lost, and the next `Save` deletes that stale aside before renaming again. **On the exception type:** a failed `Save` rethrows the underlying exception rather than a normalized one, and because the failing step is now the *move*, a locked **destination** surfaces on .NET 8 / Windows as `UnauthorizedAccessException` — which does **not** derive from `IOException` — where a locked **source** still gives `IOException`. A caller must therefore not filter on `IOException` alone; I69's classifier does not, since everything outside `FileNotFoundException`/`DirectoryNotFoundException`/`ArgumentException` falls through to `StoreFailed`, so both types report identically. (`Save`, `RenameExistingAside`, `RestoreAsides`, `TryDeleteFile`)

- **I74** — a profile's thumbnail has FOUR sources, all converging on the same store-and-attach step
  (`TryAttachThumbnail`): the **auto** capture at `IntroEnd.Snapped` when a profile is saved, the
  **upload** of a chosen image file, the **snapshot** of the frame currently on screen
  (`SnapshotProfileThumbnailAsync`, T-135), and the **re-take on apply** of a picture narrower than 320px
  (T-181, I110–I118). All four store at `ProfileThumbnailWidth`, so the stored
  picture is the same size whichever produced it. **Closer since T-169 (I106), still not literally
  true:** this was written as though already so while the upload path copied bytes verbatim, and a real
  store held 6 uploads at 64px against 4 captures at 96. T-169 brought uploads into line, but a capture
  is exactly `ProfileThumbnailWidth` wide (640 since T-172) while an upload narrower than that is kept
  narrower, and a restored picture keeps whatever width it was backed up at.
- **I75** — the snapshot grabs at `Player.Position` from the SELECTED row's file — the frame the user is
  looking at, never the intro-end the auto path uses — and is gated by `CanSnapshotProfileThumbnail`
  (a selected profile AND a selected row AND `Player.IsReady`), with `SnapshotUnavailableReason` naming
  the missing precondition rather than leaving the button inert.
- **I76** — the snapshot REPORTS its failures, like the upload and unlike the silent auto capture (I66):
  a null/throwing grab and a refused store both reach `Operation.Error`, a success retracts an earlier
  report, and a failure leaves the profile's existing thumbnail exactly as it was.

- **I77** — the **save dialog names a profile and nothing else**. It carries no thumbnail-mutating control,
  because those bind to `SelectedProfile`, which during a NEW profile's save is still the PREVIOUSLY
  selected profile — so a Clear there destroyed another profile's picture silently (T-139). All thumbnail
  editing lives in the profile bar, where `SelectedProfile` is the profile being acted on by definition.
- **I78** — the picture is **optional**: `SaveProfile` persists first and the auto-grab is best-effort
  afterwards (I66), so a profile with no thumbnail is a normal outcome, not a failed save. It can be set
  from a frame (I75), uploaded, or **removed** — all three from the bar, all after the fact.

### Durability & portability (`src/App/Settings/ProfileBackup.cs`, `packaging/VideoSplitJoiner.iss` - T-147)
- **I79** - **uninstall removes no user data.** The installer script contains no `[UninstallDelete]`
  section and names no `{userappdata}` / `{localappdata}` / `{userdocs}` path, so profiles and their
  pictures survive an uninstall/reinstall on the same machine. Asserted against the real `.iss` file, not
  assumed; if a delete is ever genuinely needed the assertion is changed deliberately.
- **I80** - a profile is stored across **two roots** (profile in Roaming, picture in Local), and this is
  deliberate and unmigrated. It is the reason the backup embeds images rather than referencing them
  (ADR-0021).
- **I81** - `Export` writes ONE self-contained file: every profile, each with its picture inline as
  base64 plus the original extension, and a `version` field. It returns the profile count and the count
  that carried an image.
- **I82** - a profile whose image is missing or unreadable is still exported, **without** the image.
  Losing a profile because its picture went missing would be a poor trade.
- **I83** - `Plan` reads a backup and reports what an import WOULD do, changing nothing on disk and
  nothing in settings. Planning the same file repeatedly does not touch it.
- **I84** - a corrupt, truncated, empty, or non-JSON file yields a failed plan carrying a user-facing
  reason - and a failed plan proposes **nothing** (`New`, `Colliding`, `Images` all empty), which is what
  makes a bad file a no-op rather than a half-applied restore. `Apply` also refuses a failed plan outright,
  as defence in depth.
- **I85** - a backup whose `version` is newer than `CurrentVersion` is **refused with a message naming
  that**, never guessed at.
- **I86** - a row with a blank name, or values the `CutProfile` constructor rejects, is skipped; one bad
  row does not condemn the file.
- **I87** - a corrupt inline image costs the **picture**, never the profile: the profile is planned and
  imported, just without a thumbnail. Likewise at apply time, a picture that cannot be written to the store
  leaves the profile in place with none.
- **I88** - import is an **upsert, never a wipe**. `Plan` separates `New` from `Colliding` (by
  case-insensitive name, matching the persistence dedup key), and `Apply` writes the colliding ones only
  when the caller says so.
- **I89** - the collision decision belongs to the caller and its default is **keep what is already
  there**: `BulkCutViewModel.ConfirmProfileOverwrite` defaults to refusing, so an unwired or half-wired
  host cannot silently overwrite a user's profiles.
- **I90** - the overwrite question is asked **only when something would actually be overwritten**;
  a collision-free import never prompts.
- **I91** - a restored picture is byte-identical to the exported one and lands in the receiving machine's
  own thumbnail store, with the profile's `ThumbnailPath` rewritten to it - a restored path never points
  at the source machine's folders.
- **I92** - the two gestures are **cancellable no-ops**: a dialog that returns nothing performs no work,
  reports no result, and raises no error.
- **I93** - export is offered only when there is at least one profile; **import is always offered**,
  because having no profiles is precisely when a restore is needed.
- **I94** - both gestures report on the screen's existing surfaces: a success sets
  `Operation.ResultSummary` with the counts (including how many existing profiles were kept), and a
  failure reaches `Operation.Error` with a headline, an actionable hint, and copyable detail - the same
  contract as the explicit thumbnail upload (I76), and for the same reason: a silent backup is
  indistinguishable from a broken button.

### Hovering a profile shows its picture big enough to recognise (T-169, 2026-09-07)
- **I101** — hovering a profile chip opens a **preview card** showing that profile's picture **at its own size**
  (T-180, G-058): the box is `clamp(PixelWidth / display scale, 320, 640)` DIPs wide and always **16:9**, so a
  picture shows one picture pixel per screen pixel from the 320-DIP minimum (`ProfilePreviewCardWidth`) up to the
  640-DIP cap (`ProfilePreviewCardMaxWidth`, pinned equal to `ProfileThumbnailWidth`), against the chip's 28px.
  `Stretch="Uniform"` inside it, so nothing is ever cropped — a 4:3 picture is centred with bars, a portrait one
  shows whole. The width is read from the loaded bitmap's `PixelWidth`, never its DPI-derived size; the display
  scale is the system scale (the app is not per-monitor DPI aware), 1.0 when it cannot be read. The text column is
  as wide as the box, and the tooltip's `MaxWidth` (650) leaves room for the 640 box. The card sets
  `UseLayoutRounding`, so the picture starts on a whole device pixel at every display scale (5 DIPs of border and
  padding are 6.25 / 7.5 device pixels at 125% / 150%, which would otherwise draw every picture pixel soft). The card also shows the **full
  name** (the chip trims it) and **where the profile cuts, in words** — two labelled lines at body size
  (`FontSizeBody`): `Intro  cuts at 00:32.0` or `none — keeps from the start`, and `Outro  cuts 01:30.0 before the
  end` or `none — keeps to the end` (an outro of 0 trims nothing, so it reads "none"; so does any intro or outro
  that displays as `00:00.0`, e.g. a one-frame outro, rather than reading "cuts 00:00.0"). A time is set apart from its
  words by colour role **and** weight (`AccentBrush` gold, SemiBold), never a tint alone, and every run of
  the readout is readable on the card (at least 4.5:1 against `Surface0`). The card shows the profile's
  own values; on each video the cut then snaps to a keyframe. A picture alone does not identify a profile; the card
  is what makes I95's promise — pictures visible *before* you choose — actually answerable
  (`ProfilePreviewBox` · `ProfilePreviewBoxSizeConverter` · `ProfileCutReadout` · `ProfileCutReadoutConverter`).
- **I102** — the card is a **`ToolTip`**, not the `Popup` the scrub bars use. Those popups **track the
  cursor** along a timeline, which is why they cannot be tooltips; hovering an item to see a card needs
  no tracking, and `ToolTipService` supplies the open delay, the dismissal and the screen-edge flip.
  `ShowDuration` is raised to defeat WPF's ~5s auto-hide (a panel you hold the cursor on to read must not
  vanish mid-look) and `InitialShowDelay` stops the card strobing as the cursor sweeps the wrapped grid
  of chips (SPEC-011 I152).
- **I103** — the card is declared in the **item `DataTemplate`**, never in the `ProfileChipItem` style,
  and takes its `DataContext` from **`PlacementTarget.DataContext`**. Both halves are load-bearing and
  both were found by testing, not by reading: a `UIElement` in a `Style` setter is a **single shared
  instance** across every item, and a `ToolTip` is a **logical** child that does not inherit the
  template's `DataContext`, so a plain `{Binding}` resolves to null. Either mistake renders a card that
  is present, correctly sized, and **completely empty** — visible to a user, invisible to any test that
  only asserts the card exists.
- **I104** — a profile with **no picture still gets a useful card**: the letterbox collapses and the name
  and values carry it, in a column of the minimum width. An empty image box is worse than none, and a profile
  without a picture is a normal outcome (I78), not a failure. Since T-180 the box collapses on the **loaded
  picture**, not the path, so a path whose file is missing or does not decode collapses it too instead of showing
  an empty frame.
- **I105** — the card **never intercepts the click that selects**. I97 requires a click to SELECT, and a
  card sitting under the cursor is exactly what would break it. The card opens **above** the chip
  (`ToolTipService.Placement="Top"`; WPF flips it below when there is no room above), so it is never between the
  cursor and the chip, and it is never focusable; both are asserted. (Corrected by the T-180 review: this used to
  say a `ToolTip` is never hit-testable. It is — the old combined assertion passed only because it is not
  focusable.)
- **I106** — **every gesture now targets one width, `ProfileThumbnailWidth` = 640** (320 from T-169, raised by
  T-172) — the width I74 has always claimed, for all four of its sources. Captures, snapshots and the re-take on
  apply (I110) grab at it through ffmpeg (`scale=640:-1`, so exactly 640 — which **enlarges** a source
  narrower than that, e.g. a 176x144 `.3gp`); an **upload is re-encoded** down to it (`ImageNormalizer.ShrinkToWidth`) instead of being copied byte-for-byte (I42's verbatim copy still
  describes the store, which now receives an already-normalized file). Measured on a real machine before
  the change: 6 of 11 stored pictures were 64px uploads against 4 captures at 96, so preview sharpness
  silently depended on how the picture had been made. **Why 640 (T-172, G-056):** the card's box is 320 DIPs, so
  320 pixels were exact only at 100% display scaling; 640 is sharp up to 200%. Measured: a single-frame grab takes
  ~119 ms at 320, 640, 960 and 1920 alike, so width costs bytes, not time — and an 11-profile backup grows from
  ~0.10–0.26MB to ~0.23–1.1MB, which is why this is a cap and not "keep the original". Since T-180 the card grows to
  the picture, so the stored width is the card's **cap**, not twice its box: a stored capture shows pixel-exact at
  100%, and a test pins the two equal (T-172's "twice the card, sharp up to 200%" rationale described a fixed 320
  card). **"Captures are exactly 640" held only on a cold cache second until T-179:** the thumbnail cache was keyed
  on the input and the second, not the width, so a second first grabbed at 64 for a row's chip handed that 64px
  file to a later 640 grab of the same second. T-179 keyed the cache on width too (SPEC-005 I26). **Not covered:** a
  picture restored from a backup (I91) is stored exactly as it was backed up and is never normalized.
- **I107** — **storage** never enlarges: **upload** normalization **only ever shrinks** (captures are not
  normalized — see I106). An upload already narrower than the target is stored untouched: inflating a 64px
  image to 640 adds bytes and no detail, turning "small but sharp" into "large and soft". **Rescoped by T-172:**
  the rule is about what is stored. The card still **fills** its box with a smaller picture, enlarging it — and
  says so (I109). It is also **best-effort**: any failure
  stores the original, because a picture that cannot be re-encoded is still a picture and refusing it
  would turn a cosmetic improvement into data loss. (`ImageNormalizer.ShrinkToWidth` returns null for both
  cases and the caller stores the original.)

### The upload gesture refuses a file that is not an image (T-170, 2026-09-07)
- **I100** — an **upload whose file is not an image is refused in words**, and nothing is copied into the
  store. Every layer was individually correct and the whole was not: the store copies bytes verbatim
  (I42), the record validates nothing (I35), and the picker offers an *All files* escape hatch with only
  `CheckFileExists` set — so a real store ended up holding a **1-byte file containing the character
  `x`** attached to a profile as its picture. The check is a **leading-byte signature test**
  (`ImageSignature.IsImage`) over exactly the formats the picker offers, and it sits on the **upload** path only: the auto and snapshot frames
  are written by ffmpeg at this app's own request, so validating them would be checking our own output.
  A **missing** pick keeps its existing, more useful message (*"could not be read … may have been moved,
  renamed or deleted"*) rather than being told it is not an image — the guard is conditioned on the file
  existing.

  Bounded deliberately: this refuses files that are **not images**; it does not certify that an image is
  undamaged. A truncated JPEG with an intact header still passes here and falls back to the placeholder
  at decode time. Saying so is the point — the invariant claims what it enforces and no more.

### The picker is a full-width wrapping bar, not a dropdown (`BulkCutView.xaml` `ProfileBar` — T-161, re-shaped T-168)
- **I95** — profiles are chosen from a **full-width bar of chips shown at rest, wrapping onto new
  lines**, not a `ComboBox`.
  Profiles carry pictures — I75/I76 and the snapshot gesture (T-135) exist precisely so they can — and a
  closed dropdown hid every one of them until after the user had already chosen by name, which is the
  one moment the picture cannot help.
- **I96** — the bar is bound to the **same two properties the ComboBox used**, `Profiles` and
  `SelectedProfile`. Selection semantics, `HasSelectedProfile`, and every apply/thumbnail/delete command
  are therefore untouched by the change — the surface moved, the model did not.
- **I97** — **a click SELECTS; it never applies.** `Apply to all` rewrites the cut points of every ticked
  row, and a bar you click straight across makes a stray click far cheaper than opening a dropdown and
  picking a row,
  so wiring apply to selection would put a bulk edit one misclick away. Asserted directly: setting
  `SelectedProfile` changes no row's intro or outro, and the apply command still does.
- **I98** — the bar spans the card's **full width and WRAPS**; it never scrolls sideways. **T-161 said
  the opposite** — *"scrolls inside a fixed cap (`MaxWidth`) rather than widening"* — and named a
  `WrapPanel` wrapping the actions onto the next line as the failure mode the cap existed to prevent.
  That is now the intended design (SPEC-011 I152): the actions have their own row, so nothing beside
  the bar can be displaced and the horizontal cap has no job left. What the cap actually bought — a
  header whose height does not grow with the profile count — is preserved by **rotating it onto the
  other axis** (SPEC-011 I153). Scrolling did not disappear; it turned 90°.

  Two mechanisms are non-obvious enough to state. A `WrapPanel` inside a `ScrollViewer` whose
  horizontal scrolling is `Auto` **or** `Hidden` is measured at **infinite width** and therefore never
  wraps — only `Disabled` makes it wrap, so that attribute is load-bearing and its mutation is dead.
  And T-161's vacuity lesson still holds verbatim: an *"is it inside the window"* check is satisfied
  by construction here, so wrapping is asserted as **≥2 distinct chip Y bands** and full width is
  asserted against the **bar** and not merely the card — the first version of that test checked only
  the card and the restored 420px cap survived it.
- **I99** — the selected chip is distinguished by **three differentiators, never a tint alone** — accent
  border, muted-accent fill, and a visible accent bar under the label. The bar is reserved with
  `Visibility="Hidden"` at rest and the border thickness is constant, so moving the selection changes no
  measurement and the row cannot twitch.

### Applying to rows still scanning keyframes (T-173, 2026-09-16)
- **I108** — **one classification, shared by every copy path.** `ApplyOutcome` classifies each applied target for
  both `ApplyProfile` and `BulkCutViewModel.ApplyToAll` (and through it the set-at-playhead fan-out). A target whose
  keyframe scan has landed is invalid iff `!IsValidCut`, exactly as before. A target **still scanning** is invalid
  only when `IsCutHopelessBeforeSnap`: both ends are handles and the requested outro is at or before the intro — a
  verdict no nearest-keyframe snap (exact ties go to the earlier keyframe), no failed-scan identity snap and no
  precision flip can reverse — and it is then also counted in `InvalidStillScanningCount`. Every other scanning
  target goes to `PendingSnapRows`, including a sub-second span a snap may still widen and a no-outro intro at or
  past `Duration`, whose upper bound does not snap while the intro does. Judging a scanning row against the 1 s
  `MinKeptSpan` floor instead (the D-005 draft) would call rescuable rows invalid (`ApplyOutcome.Applied`,
  `BulkItemViewModel.IsCutHopelessBeforeSnap`).

### The hover card fills, and says when a picture is too small to fill it sharply (T-172, 2026-09-16)
- **I109** — the card's picture box is **at least** `ProfilePreviewCardWidth` (320) DIPs wide (T-180 made it the
  minimum of a box that grows to the picture — I101), and `Stretch="Uniform"` **fills** it with any picture,
  enlarging a small one. A picture with **fewer real pixels** across than that
  width also shows a **low-resolution note** naming what re-takes it (`low resolution — applying this profile to a
  video re-takes it at full size, or use 📷 Use current frame`, T-181); a picture at or above the box width shows
  none. The card keeps filling because the request was to fill it; every picture saved before T-172 is 64–96px,
  and since T-181 the next apply of its profile re-takes it (I110) — until then the note turns a silent 3–5x blur
  into a disclosed limitation. Judged on the loaded bitmap's `PixelWidth` — never its DPI-scaled size, which WPF derives from
  file metadata — against the same named resource the box is drawn at, so the threshold is not a third
  hand-typed number (`PixelWidthBelowToVisibleConverter`; `BulkCutView.xaml` `ProfilePreviewCard`,
  `ProfilePreviewCardWidth`).
- **I110** — **applying a profile re-takes a small picture** (T-181, G-058). `ApplyProfileToSelected()` /
  `ApplyProfileToAll()` apply exactly as before — synchronously, with the same return value, `ApplyToAllReport`
  and apply note (SPEC-011 I26) — and then start **one** background re-take **only if** the applied profile's
  stored picture is a readable file narrower than `LowResolutionPictureWidth` (**320**, pinned equal to the card's
  `ProfilePreviewCardWidth`, so "the card flags it" (I109) and "applying re-takes it" are one condition), or a
  file that is **missing** (nothing left to protect). **Never** for a picture 320px or wider, a file that exists
  but cannot be measured (never replace a picture we could not measure), a profile with **no** picture (removing
  one is deliberate, I78), or any gesture other than apply — save, select, hover and backup restore never re-take
  (`StartPictureRefresh` · `PictureNeedsRefresh` · `ImageNormalizer.TryReadPixelWidth`, header-only, pixels
  never the DPI size).
- **I111** — **the source row.** Apply-to-selected uses the selected row. Apply-to-all tries the selected row first
  when it is a target, then the **other** targets from the top of the list (so a selected row in the middle that
  cannot hold the intro hands over to the first qualifying target from the top, not the one after it). The first
  of them whose probed `Duration` is **longer** than the profile's `IntroFromStart` is the source; one with no duration, or a duration not longer than the
  intro, is passed over, and if none qualifies there is no re-take. Whether the row's *cut* is valid does not
  matter for a *picture*.
- **I112** — **the time is the profile's own `IntroFromStart`** on the source row's file, at
  `ProfileThumbnailWidth` — not the row's snapped cut. It is the same for every target and needs no keyframe scan,
  so it works while a row is still scanning (T-173). The automatic default (I61) grabs the snapped intro-end; the
  two differ by less than one GOP, which does not matter for a recognition picture. The grab returns a true 640px
  frame only because the thumbnail cache is keyed on width (T-179, SPEC-005 I26). An intro of 0 grabs the first
  frame.
- **I113** — **a re-take attaches only if nothing changed while its grab was in flight.** Each profile name
  (case-insensitive) has a **picture generation**, stamped from one view-model-wide counter, so it **only ever
  increases**. It is bumped by every change to a profile's picture or existence: `TryAttachThumbnail` (every
  source — auto, upload, snapshot and this re-take), `ClearThumbnail`, `DeleteSelectedProfile` (which keeps the
  entry, so a re-created profile never lands back on an earlier value), **every** `SaveProfile` (the first
  included) and a backup restore (every name in the restored set). A path check alone is not enough: the store
  names each file after the profile, so a new small upload with the same extension keeps the same path. When the
  grab lands, the re-take is **discarded** silently unless the profile still exists, its generation is unchanged
  and its picture still qualifies (I110) — which covers *Use current frame*, any upload, *✕ Picture*, a re-save, a
  delete and a delete-then-re-create (`PictureGeneration`).
- **I114** — **the old picture is kept, never lost.** Before storing, the re-take **copies** — never moves — the
  current file, when it exists, to `profile-thumbs/replaced/<SafeFileName(name)>-<yyyyMMdd-HHmmss><ext>`
  (`ProfileThumbnailStore.KeepAside`; two copies in one second get distinct names). If that copy fails, the
  re-take stops and the old picture stays where it is. If the copy succeeds and the store's `Save` then fails,
  the old picture is still at its original path and still attached, and the redundant copy is deleted (best
  effort) — but only once the old file is verifiably back at its path: the store restores its asides best-effort,
  and deleting the copy after a double failure would lose the picture. The store's own sweeps enumerate the root only, so they never touch `replaced/`.
- **I115** — **a re-take that does not happen is silent.** A grab that returns nothing or throws, a discard
  (I113), or a keep-aside or store failure (I114) shows nothing: `Operation.Error` is never set, the apply note is
  unchanged, nothing is kept aside, and the old picture is byte-identical — like the automatic default (I66).
- **I116** — **a re-take that happens says so, once, under its own apply.** `ProfilePictureRefreshNote` reads
  `Picture for "<profile>" re-taken at full size from <file name> — the old one is kept.`, with a **Show old
  picture** button that opens Explorer on the kept copy (`ProfilePictureRefreshKeptPath`) — the folder is under a
  hidden AppData path, so naming it would not help anyone find it — or `… — its old picture file was missing.`
  with no button when there was no file to keep. It is shown in the apply-note area under `ApplyReportSummary`
  (and not part of it), on a line **reserved from the click** while the re-take is in flight for the current
  report (`IsPictureRefreshLineReserved`), so the note arriving a grab later does not push the list down under
  the pointer; a discard gives the line back. The note is cleared in the `ApplyToAllReport` setter, so every
  writer of the report clears it — both profile applies, the row-level *Apply to all*, the set-at-playhead
  fan-out, list clear and batch start. A re-take announces under the **latest apply of its profile**: re-applying
  the same profile while its grab is in flight moves the note to the newer summary rather than losing it. A
  re-take that lands after that report was replaced by any other action still attaches the picture, but its note
  is **dropped**, so it never appears under another action's summary. (The wording and the button are the T-181
  review's change to the planned sentence, which named a folder no user could find.)
- **I117** — **a re-take never moves the selection.** It attaches by name through the selection-preserving mode of
  `TryAttachThumbnail` (`keepSelection`): the bar's selection is re-pointed at the refreshed instance of whatever
  profile was selected — the one the user may have moved to while the grab was in flight — or stays empty. It
  holds with the real profile bar, whose `ListBox` binds `SelectedItem` two-way and loses it when the bar is
  re-projected. Every other attach keeps selecting the thumbnailed profile.
- **I118** — **one re-take in flight per profile name** (case-insensitive): an apply of the same profile while one
  is in flight starts nothing; a discarded or finished re-take frees the slot, so a later apply can retry.
  `PendingPictureRefresh` completes when none is in flight, and every test awaits it before asserting.

## Links
- Design: D-005 (apply a cut before the snap — built by T-173: I20/I24/I25/I27 amended, I108 added) · ADR-0021 (profiles survive reinstall by not being touched; portability via a backup file rather than a two-root migration) - (feature tasks T-096 apply-to-all convention · T-102 model/persistence/apply · T-103 VM command glue · T-106 thumbnail model/store · T-107 thumbnail UI glue · T-129 upload-failure reporting - T-147 backup/restore + installer guarantee)
- Goals: G-037, G-038 (profile thumbnails), G-051 (profiles you can keep), G-044 (thumbnail change works — and says so when it does not), G-057 (apply a cut to rows still scanning — T-173), G-056 (a sharp hover card — T-172: I74/I101/I106/I107 amended, I109 added), G-058 (the card shows the picture at its own size and says where the profile cuts — T-180: I101/I104/I105/I106/I109 amended; applying a profile re-takes a small picture — T-181: I110–I118 added, I61/I74/I106/I109 amended)
- Related specs: SPEC-008 (operation-progress-eta — owns `OperationViewModel`, incl. the additive `ReportFailure` this spec's upload path calls); SPEC-011 (bulk-cut-screen — the T-103 non-thumbnail profile commands + the T-108 per-row cut-point thumbnails); the keyframe-snap / cut-validity spec — both adjacent, out of scope here
- Key code: `src/Core/Profiles/CutProfile.cs` (`ThumbnailPath`) · `src/App/Settings/AppSettings.cs` (`CutProfiles`/`SaveProfile`/`DeleteProfile` cascade + `SettingsDto`/`CutProfileDto`) · `src/App/Settings/ProfileThumbnailStore.cs` (`Save`/`RenameExistingAside`/`RestoreAsides`/`Delete`/`DeleteByPath`/`DefaultRoot`/`SafeFileName`; `KeepAside` — the copy into `replaced/`, T-181) · `src/App/ViewModels/CutProfileApplier.cs` · `src/App/ViewModels/BulkCutViewModel.cs` (`SaveProfileWithAutoThumbnailAsync`/`UploadThumbnail`/`ClearThumbnail`/`AttachThumbnail`/`TryAttachThumbnail`/`ReportThumbnailUploadFailure`/`ClearThumbnailUploadError`/`ThumbnailAttachOutcome`; the re-take on apply — `LowResolutionPictureWidth`/`StartPictureRefresh`/`PictureNeedsRefresh`/`RefreshPictureAsync`/`PictureGeneration`/`PendingPictureRefresh`/`ProfilePictureRefreshNote`, T-181) · `src/App/Io/ImageNormalizer.cs` (`TryReadPixelWidth` — header-only pixel width, T-181) · `src/App/ViewModels/OperationViewModel.cs` (`ReportFailure` — the reporting seam) · `src/App/Views/BulkCutView.xaml.cs` (`OnUploadThumbnailClicked`, `ChooseProfileExportPath`/`ChooseProfileImportPath`/`ConfirmProfileOverwrite`) - `src/App/Settings/ProfileBackup.cs` (`Export`/`Plan`/`Apply`/`ImportPlan`) - `packaging/VideoSplitJoiner.iss` (the absence asserted by I79) - `src/App/Views/ProfilePreviewCard.cs` (`ProfilePreviewBox` / `ProfilePreviewBoxSizeConverter` — the hover card's size; `ProfileCutReadout` / `ProfileCutReadoutConverter` — its readout, T-180)
- Tests: `tests/App.Tests/ApplyDuringScanTests.cs` (T-173 — applying while a row still scans: I20, I24, I25, I27, I108) · `tests/Core.Tests/CutProfileTests.cs` · `tests/App.Tests/CutProfilePersistenceTests.cs` · `tests/App.Tests/CutProfileApplierTests.cs` · `tests/App.Tests/ProfileThumbnailStoreTests.cs` (store + `DeleteProfile` cascade, T-106; the copy-then-swap durability guarantee — I73) · `tests/App.Tests/BulkCutProfileThumbnailTests.cs` (auto-default/upload/clear, T-107; upload-failure reporting, T-129) (and app-layer `tests/App.Tests/BulkCutProfileCommandsTests.cs`) - `tests/App.Tests/ProfileBackupTests.cs` (the file format + the destructive cases, T-147) - `tests/App.Tests/BulkCutProfileBackupCommandsTests.cs` (the VM gestures + the default-keep collision contract) - `tests/App.Tests/ProfileHoverPreviewTests.cs` (the hover card, T-169; the fill, the low-resolution note and its pixel threshold, T-172; the card at the picture's own size, the cut readout and the minimum/cap pins, T-180 — I101, I104, I109) - `tests/App.Tests/ProfilePreviewCardLogicTests.cs` (the box-size function and the readout, exact — T-180) - `tests/App.Tests/ProfilePictureRefreshTests.cs` (the re-take on apply: the gate, the source row, the time, the generation re-check, keep-aside, silence, the note, the selection, one in flight — T-181, I110–I118) - `tests/App.Tests/ImageNormalizerTests.cs` / `ProfileThumbnailStoreTests.cs` (`TryReadPixelWidth`, `KeepAside` — T-181) - `tests/App.Tests/InstallerLeavesUserDataTests.cs` (I79)
