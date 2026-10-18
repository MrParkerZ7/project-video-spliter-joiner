# T-187 bench prototype (reference copy)

The measurement harness behind G-059's evidence, copied here on 2026-10-04 from the session scratchpad
where it was written, so the tickets that build on it (T-188 ports it to `bench/`, T-194 builds the
production sample-table reader from `Mp4Index.cs`) can read it after that scratchpad is gone.

- **Not built.** It is in no solution, and `Infra.cs` still holds the scratchpad's absolute paths. T-188
  replaces it with `bench/VideoSplitJoiner.Bench/`.
- **Synthetic fixtures only.** It never opened a user's video, and neither may anything ported from it.
- `IoMeter.cs` starts processes itself to read their IO counters. That breaks the FfmpegRunner /
  FfprobeRunner rule, and T-188 does not port it (a job object replaces it).
- `measure_result.json` is the measurement agent's full return: fixtures, timings, alternatives with
  their equivalence checks, ranked hotspots, and the three correctness findings (T-189, T-190, T-191).
  Quotes of code comments in it are evidence, not current claims.
