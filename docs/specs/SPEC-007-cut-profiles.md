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
serves-goal: [G-037, G-038, G-044, G-051]
updated: 2026-09-01
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
**Out:** the `OperationViewModel` lifecycle itself — state machine, progress, ETA, taskbar mapping, and the `ReportFailure` entry point's own state rules (SPEC-008); the WPF error block that renders `Operation.Error` (SPEC-011/SPEC-015); the **non-thumbnail** T-103 `BulkCutViewModel` command glue (`SaveProfile`/`ApplyProfileToSelected`/`ApplyProfileToAll`/`DeleteSelectedProfile`, the profile bar, command enable/disable) — covered by SPEC-011; the keyframe-snap and cut-validity engine behind `BulkItemViewModel.IntroEnd`/`OutroStart`/`IsValidCut` (its own spec); the per-row cut-point frame thumbnails (T-108 — SPEC-011); the non-profile `AppSettings` fields (folders, layout mode, split ratios); the WPF profile-picker view/`PathToBitmapConverter` rendering; the file dialogs and MessageBox behind the backup hooks (view glue); automatic/scheduled/cloud backup (not built - backup is a manual gesture); and any migration of existing installs between the two storage roots (explicitly rejected - ADR-0021).

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
- **I61** — `SaveProfileWithAutoThumbnailAsync(name)` auto-captures the selected row's **intro-end frame** as the profile's default thumbnail: it grabs exactly one frame at `row.IntroEnd.Snapped` (width `ProfileThumbnailWidth` = 96), copies it into the `ProfileThumbnailStore`, persists the stored path onto the profile, and re-points the bar's `SelectedProfile` at the thumbnailed instance.
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

- **I74** — a profile's thumbnail has THREE sources, all converging on the same store-and-attach step
  (`TryAttachThumbnail`): the **auto** capture at `IntroEnd.Snapped` when a profile is saved, the
  **upload** of a chosen image file, and the **snapshot** of the frame currently on screen
  (`SnapshotProfileThumbnailAsync`, T-135). All three store at `ProfileThumbnailWidth`, so the stored
  picture is the same size whichever produced it.
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

## Links
- Design: — ADR-0021 (profiles survive reinstall by not being touched; portability via a backup file rather than a two-root migration) - (feature tasks T-096 apply-to-all convention · T-102 model/persistence/apply · T-103 VM command glue · T-106 thumbnail model/store · T-107 thumbnail UI glue · T-129 upload-failure reporting - T-147 backup/restore + installer guarantee)
- Goals: G-037, G-038 (profile thumbnails), G-051 (profiles you can keep), G-044 (thumbnail change works — and says so when it does not)
- Related specs: SPEC-008 (operation-progress-eta — owns `OperationViewModel`, incl. the additive `ReportFailure` this spec's upload path calls); SPEC-011 (bulk-cut-screen — the T-103 non-thumbnail profile commands + the T-108 per-row cut-point thumbnails); the keyframe-snap / cut-validity spec — both adjacent, out of scope here
- Key code: `src/Core/Profiles/CutProfile.cs` (`ThumbnailPath`) · `src/App/Settings/AppSettings.cs` (`CutProfiles`/`SaveProfile`/`DeleteProfile` cascade + `SettingsDto`/`CutProfileDto`) · `src/App/Settings/ProfileThumbnailStore.cs` (`Save`/`RenameExistingAside`/`RestoreAsides`/`Delete`/`DeleteByPath`/`DefaultRoot`/`SafeFileName`) · `src/App/ViewModels/CutProfileApplier.cs` · `src/App/ViewModels/BulkCutViewModel.cs` (`SaveProfileWithAutoThumbnailAsync`/`UploadThumbnail`/`ClearThumbnail`/`AttachThumbnail`/`TryAttachThumbnail`/`ReportThumbnailUploadFailure`/`ClearThumbnailUploadError`/`ThumbnailAttachOutcome`) · `src/App/ViewModels/OperationViewModel.cs` (`ReportFailure` — the reporting seam) · `src/App/Views/BulkCutView.xaml.cs` (`OnUploadThumbnailClicked`, `ChooseProfileExportPath`/`ChooseProfileImportPath`/`ConfirmProfileOverwrite`) - `src/App/Settings/ProfileBackup.cs` (`Export`/`Plan`/`Apply`/`ImportPlan`) - `packaging/VideoSplitJoiner.iss` (the absence asserted by I79)
- Tests: `tests/Core.Tests/CutProfileTests.cs` · `tests/App.Tests/CutProfilePersistenceTests.cs` · `tests/App.Tests/CutProfileApplierTests.cs` · `tests/App.Tests/ProfileThumbnailStoreTests.cs` (store + `DeleteProfile` cascade, T-106; the copy-then-swap durability guarantee — I73) · `tests/App.Tests/BulkCutProfileThumbnailTests.cs` (auto-default/upload/clear, T-107; upload-failure reporting, T-129) (and app-layer `tests/App.Tests/BulkCutProfileCommandsTests.cs`) - `tests/App.Tests/ProfileBackupTests.cs` (the file format + the destructive cases, T-147) - `tests/App.Tests/BulkCutProfileBackupCommandsTests.cs` (the VM gestures + the default-keep collision contract) - `tests/App.Tests/InstallerLeavesUserDataTests.cs` (I79)
