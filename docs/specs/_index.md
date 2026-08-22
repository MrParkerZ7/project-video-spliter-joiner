# Feature Specs — Index

Living-spec layer per [../standards/feature-spec-structure.md](../standards/feature-spec-structure.md).
One spec per feature; numbered invariants are the source `todo-automate` derives test cases from.

## Structure
- **Shape:** flat `docs/specs/SPEC-NNN-<slug>.md` (ids stable).
- **Template:** [_TEMPLATE.md](_TEMPLATE.md) · **Standard:** [../standards/feature-spec-structure.md](../standards/feature-spec-structure.md)

## Specs

| Spec | Slug | Area | Invariants | Covered | Gaps |
|------|------|------|:--:|:--:|:--:|
| [SPEC-001](SPEC-001-stream-copy-split.md) | stream-copy-split | core | 38 | 25 | 13 |
| [SPEC-002](SPEC-002-bulk-trim-engine.md) | bulk-trim-engine | core | 32 | 29 | 3 |
| [SPEC-003](SPEC-003-join-concat.md) | join-concat | core | 31 | 19 | 12 |
| [SPEC-004](SPEC-004-media-probe.md) | media-probe | core | 32 | 22 | 10 |
| [SPEC-005](SPEC-005-thumbnail-service.md) | thumbnail-service | core | 25 | 20 | 5 |
| [SPEC-006](SPEC-006-waveform-service.md) | waveform-service | core | 23 | 21 | 2 |
| [SPEC-007](SPEC-007-cut-profiles.md) | cut-profiles | core | 30 | 28 | 2 |
| [SPEC-008](SPEC-008-operation-progress-eta.md) | operation-progress-eta | app | 40 | 36 | 4 |
| [SPEC-009](SPEC-009-app-settings.md) | app-settings | app | 22 | 18 | 4 |
| [SPEC-010](SPEC-010-split-screen.md) | split-screen | app | 40 | 36 | 4 |
| [SPEC-011](SPEC-011-bulk-cut-screen.md) | bulk-cut-screen | app | 60 | 52 | 8 |
| [SPEC-012](SPEC-012-join-screen.md) | join-screen | app | 28 | 24 | 4 |
| [SPEC-013](SPEC-013-preview-player.md) | preview-player | app | 48 | 44 | 4 |
| [SPEC-014](SPEC-014-timeline.md) | timeline | app | 35 | 29 | 6 |
| [SPEC-015](SPEC-015-app-shell-theming.md) | app-shell-theming | ui | 28 | 15 | 13 |
| **TOTAL** | | | **512** | **418** | **94** |
