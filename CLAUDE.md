# P2PShare

A file-sharing client in the spirit of Napster/Kazaa/eMule, but search is powered by passively
crawling the BitTorrent mainline DHT instead of any central server. Full design rationale is in
`README.md` — read that first for the "how search actually works" section.

## Status

MVP is complete and was built/verified in a network-sandboxed cloud container (see "What's never
been tested" below — this is likely the first environment that can actually test it for real).
Verified there: full solution build, every UI tab screenshotted and working end-to-end under Xvfb,
real local torrent creation + SHA-1 hashing + seeding via MonoTorrent, graceful timeout handling for
unreachable info-hashes, SQLite/FTS5 search index round-tripping correctly.

## Build / run

Requires the **.NET 10 SDK** (`dotnet --version` should show `10.x`). On Ubuntu/Debian, `apt-get
install dotnet-sdk-10.0` works if the Microsoft package feed isn't registered; otherwise use the
official install script.

```bash
dotnet build
dotnet run --project src/P2PShare.Desktop
```

Settings, the search database, and MonoTorrent's cache (fast-resume, DHT node cache) live under
`~/.p2pshare/`. Downloaded/shared files default to `~/P2PShare Downloads/`.

## Architecture

- `src/P2PShare.Core/` — no UI dependency, this is where almost all the interesting logic is.
  - `Dht/DhtCrawler.cs` — a from-scratch, minimal Mainline DHT (BEP 5) node using
    `MonoTorrent.BEncoding` for message encode/decode. It bootstraps from the public routers, does
    `find_node` random walks to grow its visibility, and — this is the actual search mechanism —
    records the info-hash out of every `get_peers`/`announce_peer` **query** it receives from other
    real nodes (not responses to its own queries). This is deliberately a separate DHT
    implementation from MonoTorrent's own (see below for why).
  - `Engine/TorrentEngine.cs` — thin wrapper around `MonoTorrent.Client.ClientEngine`. Uses
    `engine.DownloadMetadataAsync(magnetLink, token)` to resolve name/size/files for a hash the
    crawler observed (BEP 9 over the wire, via whatever peers MonoTorrent's *own* DHT finds — this
    is MonoTorrent's internal DHT, separate from the crawler). Also handles real downloads, and
    creates/seeds torrents from local files via `MonoTorrent.TorrentCreator` +
    `MonoTorrent.TorrentFileSource`.
  - `Search/SearchIndex.cs` — SQLite + FTS5 (falls back to `LIKE` if FTS5 isn't compiled into the
    SQLitePCLRaw provider in use) local index. Nothing here is network-facing.
  - `P2PShareService.cs` — the only class the UI talks to. Wires crawler → bounded-channel resolver
    worker pool → search index, and re-seeds `Settings.SharedPaths` on startup.
- `src/P2PShare.Desktop/` — Majorsilence.Forms UI (desktop/Avalonia backend only so far — see
  README's "Other platforms" section for how to add Android/WASM/iOS heads from the same code).

### Why a second, hand-rolled DHT implementation instead of just using MonoTorrent's?

MonoTorrent's `DhtEngine` (`engine.Dht`) is built for "find peers for a torrent I already know the
hash of" — exactly what `TorrentEngine` uses it for. It has no public hook for observing *arbitrary*
incoming queries for hashes it isn't tracking, which is what indexing/search needs (the same
technique projects like `magnetico` use). `DhtCrawler` fills that gap: its own UDP socket, acting as
a normal-enough DHT citizen to get real traffic routed to it, logging what it sees.

## Non-obvious gotchas found the hard way

- **Majorsilence.Forms (Avalonia backend) docks children in the order they're added — opposite of
  classic WinForms convention.** A `Dock = DockStyle.Fill` control must be added to `Controls`
  *first*, then `Dock.Top`/`Dock.Bottom` bars added after it — otherwise the fill control's header
  chrome (confirmed specifically: a `TabControl`'s tab-strip) silently fails to render, with no
  error. See `MainForm.cs` for the working order. This cost real time to isolate — don't "fix" it
  back to WinForms order without re-testing under a real display.
- `System.Drawing.Color`/`Point`/`Size`/`Rectangle` are the real cross-platform
  `System.Drawing.Primitives` types and work with a plain `using System.Drawing;`. But
  `ContentAlignment`, `Font`, `Image`, `Bitmap` etc. are **not** — those are Majorsilence's own
  reimplementations (`Majorsilence.Forms.ContentAlignment`, `Majorsilence.Forms.Drawing.Font`, ...).
  Writing `System.Drawing.ContentAlignment` compiles-fails with a forwarded-assembly error; use the
  bare name with `using Majorsilence.Forms;` in scope instead.
- `Majorsilence.Forms.Timer` collides with `System.Threading.Timer` the instant `ImplicitUsings` is
  on (it is, by default). Alias it: `using Timer = Majorsilence.Forms.Timer;` in any file that uses
  a UI timer.
- The framework is genuinely beta (NuGet `Majorsilence.Forms` 26.11.18 at time of writing). If a
  control behaves oddly, don't assume it's your code first — reflect on the installed assembly
  (`typeof(Majorsilence.Forms.Whatever).Assembly.GetExportedTypes()` / `GetMethods()`) to check the
  real API surface rather than trusting memory of classic WinForms, and sanity-check suspicious
  layout behavior in isolation (a 10-line throwaway Form) before assuming your composition is wrong.
- No visual designer exists yet for this framework — everything is hand-written control composition
  in code, same as this project already does.

## What's never been tested (do this first in a normal environment)

The cloud sandbox this was built in only allows outbound HTTPS to an explicit host allowlist (even
DNS resolution is filtered to it) — no path for the arbitrary UDP/TCP to unpredictable peer IPs that
DHT and BitTorrent fundamentally need. So the entire *networked* half of this app has only been
verified by code review and local-only testing, never against the real public DHT:

- Does `DhtCrawler` actually bootstrap and get real `get_peers`/`announce_peer` traffic routed to it
  on a normal network?
- Does `TryFetchMetadataAsync` actually resolve real torrents found that way?
- Does a real download actually complete end-to-end (peer connections, piece verification, file
  assembly)?
- Bandwidth/port settings in the Settings tab only take effect on restart (by design, documented in
  the UI) — confirm that's still true and not confusing in practice.

If you're running this locally with normal internet access, this is the first real chance to find
out. Start on the Network tab and watch the node count / activity log; a non-zero, growing node
count within the first ~30 seconds means bootstrapping is working.

## Known gaps / not yet built

- No automated tests (unit or integration) — everything so far was verified by manual build +
  screenshot + a couple of throwaway Core-level smoke scripts, not a committed test suite.
- Android/iOS/WebAssembly heads aren't scaffolded (README explains how to add them via
  `dotnet new majorsilenceforms --IncludeAndroid --IncludeWasm --IncludeiOS`).
- `DhtCrawler`'s routing table is a flat capped dictionary, not real Kademlia k-buckets — fine for a
  crawler (the goal is maximum exposure to passing traffic, not efficient routing), but worth
  knowing if you extend it.
- `_attempted` (dedup set in `P2PShareService`) grows unbounded for the life of the process; fine
  for a session, would want bounding for a long-running daemon.
