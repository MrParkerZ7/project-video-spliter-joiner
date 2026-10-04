# 2026-10-05 — drain paused (todo-pause)

- **What paused:** the `todo-next-auto` drain of G-059, at a **ticket boundary** (stop point a): T-189 finished its
  build, review and commit (`2f33f6e`) and was marked done; nothing was mid-flight, the tree is clean, no ticket is
  left in progress.
- **Landed this drain:** T-187 clarified → G-059 (`bd06844`); T-188 bench (`f73cd8a`); T-189 HEVC Exact fallback
  (`2f33f6e`). Filed: T-221, T-222, T-223, T-224. Not pushed yet.
- **Resume:** `todo-next-auto` (or `todo-next-all`). The planned order continues T-193 → T-219 → T-194 → T-190 →
  T-191 → T-192 → T-223 → T-224 → T-195 → T-196 → T-222 → T-221 → T-197, then one settings-isolated relaunch of the
  app on synthetic files.
