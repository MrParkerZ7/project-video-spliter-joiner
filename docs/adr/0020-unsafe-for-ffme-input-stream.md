# ADR 0020: Allow unsafe blocks in the app, to open files whose path cannot be a URI

## Status

Accepted — 2026-08-30 (T-132, epic G-045)

## Context

A user reported *"I can't do multi bulk cut, Invalid URI: The hostname could not be parsed."*

`FfmeMediaPlayer` opened every file with `new Uri(path, UriKind.RelativeOrAbsolute)`. That constructor
throws when a UNC path's **server name is not a legal URI hostname** — most realistically because it
contains a space, which consumer NAS boxes routinely have (`\\Seagate NAS\…`, `\\My Cloud\…`). Probed on
.NET 8:

| path | result |
|---|---|
| `C:\videos\a.mp4`, `Z:\Videos\ep 1.mp4` | OK |
| `\\NAS\…`, `\\192.168.1.5\…`, `\\my-nas\…`, `\\my.nas.local\…` | OK |
| `\\my nas\…`, `\\Seagate NAS\…`, `\\host:port\…`, `\\host[1]\…` | **throws** |

The cutting engine is unaffected — it passes raw paths as process arguments and never builds a `Uri`. What
broke was the preview, and with it `CanSetCutAtPlayhead`, which is how cuts are normally placed. T-131
made the failure legible; this decision is about making it stop being a failure.

**There is no smarter `Uri` to build.** A host containing a space cannot be a URI authority at all. Every
construction was probed and all throw identically: `RelativeOrAbsolute`, `Absolute`, `UriBuilder`,
`file:` with two or four slashes, and a percent-encoded host (`%20` is not legal in a host either).

FFME exposes exactly two entry points:

```
MediaElement.Open(Uri)                 — unreachable for these paths
MediaElement.Open(IMediaInputStream)   — addresses the file directly
```

`IMediaInputStream` is an ffmpeg AVIO adapter, so its callbacks take raw pointers:

```csharp
int  Read(void* opaque, byte* targetBuffer, int targetBufferLength);
long Seek(void* opaque, long offset, int whence);
```

Implementing it requires `AllowUnsafeBlocks`, which this project has never enabled.

## Decision

Enable `AllowUnsafeBlocks` in `VideoSplitJoiner.App`, and confine the unsafe surface to a single file,
`Media/FileMediaInputStream.cs`.

The player picks a route per path: a path that can be a `Uri` still opens with `Open(Uri)`, exactly as
before; only a path that cannot goes through the stream adapter. The new code therefore runs **only where
the app is currently broken**, which is what makes enabling unsafe an acceptable trade rather than a
general loosening.

## Alternatives considered

- **Keep refusing, document the workaround** (what T-131 ships). Honest, and mapping the share to a drive
  letter genuinely works — but it asks the user to reconfigure Windows to use a video editor.
- **Auto-map a drive letter** (`WNetAddConnection2`). Invasive, needs cleanup on exit, can collide with
  the user's own mappings, and fails without the right permissions.
- **Rewrite the host to its IP.** `\\192.168.1.5\…` does parse, but resolving a NetBIOS name containing a
  space is not reliably possible, and silently rewriting the user's path is surprising.
- **Copy to a local temp first.** Unacceptable for video files.

## Consequences

**Good.** Preview, seeking and duration work from any local or network path the OS can open, regardless of
whether the path survives URI parsing. The set-at-playhead gestures come back on those shares.

**Cost.** The project now compiles with unsafe enabled, so nothing structurally prevents pointer code
appearing elsewhere. Mitigated by convention, not by the compiler: the unsafe surface is one file, stated
in the csproj comment and here. The pointer work itself is one `Span<byte>` wrap over the supplied buffer
— no manual arithmetic.

**Risk we are accepting knowingly.** The adapter is exercised by unit tests against real local files, but
its actual purpose — a real network share with a space in the name — **has not been verified end to end**,
because no such share is available on the development machine. The route is fail-safe by construction (it
only handles paths that currently produce an error, and falls back to T-131's message if the file cannot
be opened at all), but "it plays from a real NAS" remains unproven and should not be claimed until someone
has tried it.

**Handles.** The stream keeps the file open, so it is disposed on `Unload` — a held handle would block a
later cut over the same file. It is opened `FileShare.ReadWrite` so the preview can never be the reason a
cut is refused.

## Links

- T-131 (make the failure legible), T-132 (this), G-045
- SPEC-013 I49–I52 (which paths the player can address, and what it says when it cannot)
- `src/App/Media/FileMediaInputStream.cs`, `src/App/Media/MediaSourceUri.cs`,
  `src/App/Media/FfmeMediaPlayer.cs`
