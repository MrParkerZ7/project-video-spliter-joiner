# ADR 0023: A drag with nothing usable in it stays refused at the cursor, not accepted and then explained

## Status

Accepted — 2026-09-04 (T-154)

## Context

T-154 set out to stop the app discarding dropped files in silence. Bulk Cut got a `DropSummary` line first;
Split and Join were left out of scope and were finished in this change. Mirroring the shipped screen
exposed a claim in the spec that the code cannot deliver, and the correction is a decision rather than a
patch.

All three screens gate acceptance in `DragOver`:

```csharp
if (e.Data.GetDataPresent(DataFormats.FileDrop)
    && e.Data.GetData(DataFormats.FileDrop) is string[] paths
    && VideoFileFilter.HasAnyVideo(paths))      // <-- the gate
{
    e.Effects = DragDropEffects.Copy;  ...
}
else { e.Effects = DragDropEffects.None; ... }
```

When a drag holds **no** recognised video, `Effects` is `None`. Windows then shows a no-entry cursor and
**never delivers a drop event** — no handler runs, no view-model code executes, and there is nothing that
could set a message. `DropSummary` is unreachable for that case by construction.

SPEC-011's **I122** nonetheless listed *"an unrecognised extension"* among the paths `DropSummary`
explains, and used the example *"3 files were not added: 2 are not video files, 1 is already in the list"*.
The unrecognised-extension case is only reportable as part of a **mixed** drop, where at least one video
opens the gate.

Two things kept the overstatement invisible:

1. **The 21 shipped tests call `AddDroppedFilesAsync` directly**, which bypasses the `DragOver` gate the
   real gesture goes through. The view-model counts correctly; the user simply never gets to reach it.
2. The existing suite even *documents* the gate in passing — `CommonContainersAreAccepted` asserts
   `HasAnyVideo` is true "otherwise the drag shows a no-entry cursor and the drop never happens" — so the
   knowledge was present and the contradiction still went unnoticed.

## Decision

**Leave the gate alone. Treat the no-entry cursor as that case's feedback, and say so in the specs.**

- `DragOver` keeps refusing a payload with no recognised video.
- The boundary is recorded as an invariant on every screen — **SPEC-010 I48**, **SPEC-012 I37**,
  **SPEC-011 I143** — so it reads as a known limit rather than a bug waiting to be rediscovered.
- **SPEC-011 I122 is corrected in place** rather than quietly reworded: it now lists only the paths that
  are genuinely reportable (the non-video half of a mixed drop, a file already in the list, a folder, the
  same path twice in one payload) and states why the fourth is not.
- The User Guide gains the user-facing half: a no-entry cursor means nothing in the drag is a recognised
  video, and that is the answer.

The rejected alternative is the obvious one: accept any `FileDrop` payload so the refusal can be explained
in words.

## Consequences

**Good**

- The cursor is immediate, unambiguous, and costs nothing — it appears *during* the drag, before the user
  commits, which is strictly earlier feedback than a note after the fact.
- No behaviour change to the drag cursor on three screens, and no risk to Join's internal clip-reorder
  drag, which is distinguished from an external file drop by clipboard format.
- The specs now describe what the code does. The `command-analytics`
  *doc-promises-code-doesn't-deliver* check exists for exactly this class, and this instance is closed
  rather than carried.

**Bad / accepted**

- Dragging a folder, or a `.txt`, gives a cursor and no words. A user who does not connect the cursor to
  "wrong file type" gets less than the mixed-drop case gives them. Accepted: the alternative is worse
  (below), and the User Guide now names it.
- The asymmetry is real — drop one video plus one `.txt` and you are told; drop the `.txt` alone and you
  are not. It is defensible (the app can only speak about drops it receives) but it is an asymmetry, and
  it is written down rather than smoothed over.

## Alternatives considered

- **Accept any `FileDrop` in `DragOver`, then refuse in words.** This is the change that would make I122's
  original claim true, and it was rejected on its own merits, not on cost. It would show a **copy cursor**
  over files the app has already decided to reject — promising acceptance during the drag and withdrawing
  it after the drop. Replacing honest early feedback with a misleading affordance plus a late apology is a
  downgrade. It would also touch the drag behaviour of all three screens, including the surface that
  distinguishes Join's internal reorder from an external add, to serve the case where the user has the
  least doubt about what happened.
- **Accept only when the payload is FileDrop *and* contains at least one file the app might handle.** That
  is what `HasAnyVideo` already is; any broadening lands back in the option above.
- **Probe-based acceptance instead of an extension allowlist** — hand everything to ffprobe and let it
  decide. Genuinely better at the *classification* question and already noted in T-154 § Design 4, but it
  cannot run during a drag (a probe per file per `DragOver` tick is not viable), so it does not resolve
  this decision. Still open on its own merits.
- **Say nothing and leave I122 as written.** Rejected: a spec that claims a guarantee the code cannot
  provide is worse than no spec, and this one had already survived one full ticket.
