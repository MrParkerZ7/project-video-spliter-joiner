# 2026-10-06 — drain paused (owner's stop)

- **What paused:** the `todo-next-auto` drain, batch 2 (`wf_b7f89046-30d`), mid-build of **T-219** (stop point c:
  red, no commit). Its WIP stays uncommitted in the working tree (a commit would hide half the change from the
  review and land steps); a patch copy sits in the run's checkpoint folder. T-194, T-225, T-228, T-230 never started.
- **Landed this drain so far:** T-193 (`079304f`), T-195 waveform half (`69e5b08`), T-221 (`6600fbc`); filed T-225,
  T-226, T-227, T-228, T-229 (later), T-230, T-231 (later). Not pushed yet.
- **Resume:** `resume` in the owning session (replays the run), or `todo-next-auto` (T-219 is picked first as ⏸).
  Then T-194 · T-225 · T-228 · T-230 → T-190 · T-196 · T-222 · T-226 · T-227 → T-191 → T-192 · T-223 · T-224 → T-197,
  then one push and one settings-isolated relaunch of the app on synthetic files.
