# ADR 0022: Bin files through the shell API directly, not through VB's FileIO helper

## Status

Accepted — 2026-09-02 (T-155)

## Context

A user reported that "Delete originals" worked but interrupted them every single time:

> currently delete original files is work, but everytime windows will popup unable to remove file able,
> but remove original files still work.

The disposer called:

```csharp
FileSystem.DeleteFile(backupPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
```

`Microsoft.VisualBasic.FileIO.UIOption` has exactly two values — `AllDialogs` and `OnlyErrorDialogs`.
**There is no "no dialogs".** We were already on the quieter one, and it still puts a shell error dialog on
screen whenever a file cannot be removed. The behaviour was not configurable away; it is what that API
does.

That also explained the puzzling half of the report — *"but remove original files still work"*. The dialog
offers a retry, or the handle closes while it is up, so the deletion completes **after** the interruption.
The outcome was right and the experience was wrong.

Two things made this worth a decision rather than a patch:

1. **The app was already better placed to report than Windows was.** The Bulk Cut screen re-checks each
   row at deletion time and reports `Sent N to the Recycle Bin. Still in use: …` in its own words. It never
   needed a shell modal to tell the user anything — and a failure the app can describe is strictly better
   than a modal it cannot control, suppress, or test.
2. **Recoverability is the whole safety argument for the feature.** "Delete originals" is only defensible
   because the file goes to the Recycle Bin and can be restored. Whatever replaced the helper had to keep
   that property provably, not incidentally.

## Decision

**Call `SHFileOperation` directly, with the no-UI flags, and own the failure path.**

```csharp
fFlags = FOF_ALLOWUNDO | FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR;
```

- **`FOF_ALLOWUNDO` is what routes the file to the Recycle Bin.** Without it the same call deletes
  permanently — the file still disappears, so every "did it vanish?" assertion still passes. That single
  bit is the difference between a safe feature and an unrecoverable one, so it is asserted directly rather
  than only through its effect, and a mutation dropping it fails a test.
- **Success is verified, never assumed.** `SHFileOperation` can return 0 in cases where the file survives,
  and the caller reports a count to the user. `TryRecycle` therefore checks the file is actually gone.
- **A bounded retry** (3 attempts, 120ms) covers the common case of a handle about to close — a frame grab
  or probe finishing just after the batch. Deliberately short: trading a dialog for a frozen app is not an
  improvement.

**And name the holder rather than guess at it.** The reporter's real question was whether the app could
release the file itself. T-145 had already released the preview's handle, so a refusal on *every* run meant
something else held it. `FileLockOwner` asks the Windows **Restart Manager** and puts the process name in
the summary — `Still in use: ep1.mp4 (held by ffmpeg.exe)`. If the holder is ours, that is a bug with an
address; if it is an antivirus scanner or the shell's thumbnail cache, no amount of releasing our own
handles would ever have helped, and the user can see that instead of watching the app retry.

## Consequences

**Good**

- No dialog on any path, success or failure. The destructive action is confirmed once, by us, in our words.
- Refusals are reported *with a culprit*, which turns a dead end into something actionable.
- The failure path became testable: a real `FileShare.None` lock now produces an assertable `false` instead
  of a modal that a test cannot see, let alone dismiss.
- It unblocked [[T-156]] (auto-delete after a batch) — automating a step that interrupts on every run would
  have been worse than leaving it manual.

**Bad / accepted**

- A hand-written P/Invoke instead of a framework helper. That cost is real and was paid immediately: the
  first version carried `Pack = 1` on `SHFILEOPSTRUCT`, which on x64 removes the padding after `wFunc` so
  shell32 reads `pFrom` from the wrong offset — an `AccessViolationException` that killed the test host.
  Worse, the run still printed `Passed! … 5 of 5`, because the host died after five cases and the summary
  described only what had finished. **A green line from a crashed process is not a pass**, and this struct
  is now commented with why the packing must stay natural.
- `SHFileOperation` is the older API; `IFileOperation` (COM) is the modern one. It was not chosen: for a
  single-file recycle with no UI, `SHFileOperation` is a dozen lines against a COM interface with an
  advise-sink, and the extra surface buys nothing here. Revisit if bulk or transactional semantics are ever
  needed.
- Restart Manager is a diagnostic, not a guarantee: it names processes that *registered* the file, so a
  holder can go unnamed. It is best-effort by design and never affects whether the delete is attempted.

## Alternatives considered

- **Keep `FileSystem.DeleteFile` and pre-release our own handles.** Insufficient on its own — it would
  reduce how often the dialog appeared without removing the possibility, and the dialog is unacceptable at
  any frequency for an action the user already confirmed. (Releasing our handles is still right, and T-145
  did it.)
- **Delete permanently instead of binning.** Simpler, and no lock dialog for a file we can unlink — but it
  discards the undo that makes the feature safe to offer at all. Rejected outright.
- **Swallow the failure silently.** Then "Sent 3 to the Recycle Bin" could be a lie, and the user would
  discover the discrepancy by finding files still on disk. The count is the confirmation; it has to be true.
