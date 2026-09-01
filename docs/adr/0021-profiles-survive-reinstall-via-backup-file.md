# ADR 0021: Profiles survive reinstall by not being touched — and travel by backup file, not by migration

## Status

Accepted — 2026-09-01 (T-147, epic G-051)

## Context

A user asked for profile settings to be *"saved and restore able even after reinstall app again"*.

Investigating produced two findings, and they pull in different directions.

**1. Reinstall was already safe — by accident.** `packaging/VideoSplitJoiner.iss` has no
`[UninstallDelete]` section and names no user-profile path, so uninstalling removes the program files and
leaves `settings.json` and `profile-thumbs/` alone. Reinstalling finds the profiles exactly where it left
them. Nothing asserted this, however: a future installer edit could start deleting app-data and the first
person to find out would be someone who lost their profiles on an upgrade.

**2. A profile is stored across two roots.**

| part | folder | resolved by |
|---|---|---|
| the profile (name, intro, outro, thumbnail **path**) | Roaming `%APPDATA%` | `AppSettings.DefaultFilePath` |
| its picture | Local `%LOCALAPPDATA%` | `ProfileThumbnailStore.DefaultRoot` |

Roaming vs Local is the correct Windows distinction for these two things in isolation — settings roam,
caches do not — but a profile's picture is not a cache: it is content the user chose. The split has a
sharp edge. Anyone who backs up "their settings", or moves to a new PC via a roaming profile, keeps every
profile and loses every picture — and not visibly, because each profile arrives with a `ThumbnailPath`
pointing into a folder that does not exist on that machine, so it silently falls back to the placeholder.

The obvious repair is to move the pictures under Roaming beside the settings. That was rejected: it means
a migration on an existing install — read the old root, copy, rewrite every stored path, handle a partial
copy, handle a half-migrated state if the app is killed midway — and the payoff is only that a *manual*
folder copy would carry pictures too. That is real data-loss risk in exchange for a workflow nobody was
asked to perform.

## Decision

**Leave both roots exactly where they are. Make the guarantee explicit, and make export/import the
supported way to move or keep profiles.**

1. **The installer's hands-off behavior becomes a stated invariant, not an accident.**
   `InstallerLeavesUserDataTests` reads the real `.iss` file and asserts it contains no
   `[UninstallDelete]` and names no `{userappdata}` / `{localappdata}` / `{userdocs}` path. If one is ever
   genuinely needed, that assertion must be changed deliberately.

2. **`ProfileBackup` writes ONE self-contained file with the images inline as base64.** One file is the
   whole story: no relative paths, no sibling folder to remember, no question of which of two roots a
   picture came from, and nothing to unzip. Thumbnails are ~96px, so the size cost is negligible.

3. **Import is an upsert that plans first.** `Plan()` reads the file and reports what *would* happen
   without changing anything; `Apply()` acts on that plan. A corrupt, truncated, or future-version file
   fails at the planning stage, so a bad file changes nothing rather than half-applying.

4. **A name collision is the user's decision, never the app's.** `Plan` separates `New` from `Colliding`;
   the VM's `ConfirmProfileOverwrite` hook defaults to **keeping what is already there**, so an unwired or
   half-wired host cannot silently overwrite profiles. "Restore" being the most dangerous button in the
   app would be an unusually cruel bug.

## Consequences

**Good**

- Reinstall-safety is now a test, so it cannot regress silently.
- Profiles move between machines, survive a wiped disk, and can be shared — none of which "we don't delete
  your folder" ever covered. The user asked for reinstall; the file is what they actually needed.
- No migration, so no existing install is at risk from this change.
- A picture that cannot be read at export, or cannot be written at import, costs the picture only — never
  the profile.

**Bad / accepted**

- The two-root split remains, so a manual copy of `%APPDATA%` still silently loses pictures. Mitigated by
  the backup file being the documented path, not by fixing the split.
- Backup is manual. Nothing is automatic, scheduled, or cloud-synced; if someone never presses the button
  and their disk dies, the profiles are gone. Automatic backup is a bigger feature and is not being
  smuggled in under this one.
- The backup format is versioned (`version: 1`) and a newer file is refused rather than guessed at, which
  means an older build cannot read a newer backup. That is the intended trade — mangling data is worse
  than refusing it.

## Alternatives considered

- **Migrate the thumbnails into Roaming and back up by folder copy.** Rejected above: migration risk on
  every existing install, and it still leaves the user without a portable single file.
- **Zip the settings file and the thumbnail folder together.** More moving parts (archive handling, path
  entries, partial extraction) for the same outcome as inline base64, at this size.
- **Store the images as base64 inside `settings.json` itself.** Would make the settings file grow without
  bound and be rewritten on every unrelated setting change. The backup is the right place for inline
  bytes; the live store is not.
- **Do nothing, and answer "it already survives reinstall".** True, and beside the point — it was true by
  accident, untested, and did not cover a new PC.
