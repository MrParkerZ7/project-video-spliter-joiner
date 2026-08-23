# Documentation

The documentation portal for **VideoSplitJoiner** — a Windows WPF tool that splits, joins, and bulk-trims
video **losslessly** (ffmpeg stream-copy, keyframe-snapped, no re-encode). Start here to find any doc.

## Start here
- **[../README.md](../README.md)** — project overview + quick start.
- **[USER_GUIDE.md](USER_GUIDE.md)** — how to use the app (Split · Join · Bulk Cut).
- **[../CHANGELOG.md](../CHANGELOG.md)** — release history.
- **[GLOSSARY.md](GLOSSARY.md)** — domain terms (keyframe-snap, stream-copy, kept-segment, cut profile, …).

## Architecture & design
- **[ARCHITECTURE.md](ARCHITECTURE.md)** — as-built structure: Core (engines) ↔ App (WPF-free VMs + views), the reuse map.
- **[adr/README.md](adr/README.md)** — Architecture Decision Records (0001–0016): why stream-copy-only, FFME over
  MediaElement, hand-rolled MVVM, the bulk-trim-reuses-split decision, licensing, and more.
- **[design/](design/)** — sealed feature designs `D-001`…`D-004` (vertical mode · audio waveform · 50/50 panel · Bulk Cut).

## Specifications (living specs)
- **[specs/_index.md](specs/_index.md)** — the living-spec layer: one `SPEC-NNN` per feature, numbered invariants,
  `serves-spec:` test traceability (15 specs · ~97% covered). The source `todo-automate` derives test cases from.
- **[standards/_index.md](standards/_index.md)** — project standards (e.g. the Feature-Spec Structure standard).

## Contributing / development
- **[DEV.md](DEV.md)** — build, run, test, and the codebase conventions (hand-rolled MVVM, TDD + Case-Coverage, ADRs).
- **[ROADMAP.md](ROADMAP.md)** — shipped features + deferred/future work.

## Map of the doc set
```
README.md            ← this portal
USER_GUIDE.md        ← user-facing guide
ARCHITECTURE.md      ← as-built architecture
DEV.md               ← build / test / conventions
GLOSSARY.md          ← domain terms
ROADMAP.md           ← status matrix (shipped + future)
adr/                 ← 16 ADRs + index (design decisions)
design/              ← D-001..D-004 sealed feature designs
specs/               ← 15 SPEC-NNN living specs + standard-conformance + _index
standards/           ← project standards + index
```

> The active work board (tasks/goals) lives under `docs/todo/` and is the day-to-day planning surface;
> this portal covers the durable project documentation.
