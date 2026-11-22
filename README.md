# P2PShare

A file-sharing client in the spirit of the late-90s/2000s era — Napster, Kazaa, eMule — rebuilt on
the BitTorrent mainline DHT instead of a central server. There is no index you connect to and no
company running it: **search results come from a local index this app builds itself**, by joining
the public DHT and watching real traffic pass through it.

Built with [Majorsilence.Forms](https://forms.majorsilence.com/) (a WinForms-compatible, Avalonia/
Uno/GTK4-backed cross-platform UI toolkit — desktop today, with Android/iOS/WebAssembly heads the
same codebase can target) and [MonoTorrent](https://github.com/alanmcgovern/monotorrent) (the
BitTorrent engine: real DHT peer discovery, magnet links, piece transfer, torrent creation).

## How search actually works

Classic Napster/Kazaa/eMule each had their own kind of central or semi-central index. This app has
none. Instead:

1. **`P2PShare.Core/Dht/DhtCrawler.cs`** is a small, self-contained Mainline DHT (BEP 5) node. It
   bootstraps from the public routers (`router.bittorrent.com`, etc.), does random-walk `find_node`
   lookups to make itself known to more of the network, and — this is the actual search mechanism —
   **passively observes the `get_peers` and `announce_peer` queries other real nodes send it**. Every
   such query carries an info-hash, which means "a torrent with this hash is active somewhere on the
   network right now." This is the same technique open-source DHT indexers like
   [magnetico](https://github.com/boramalper/magnetico) use; it needs no cooperation from anyone and
   scrapes no website.
2. For each newly observed info-hash, **`P2PShare.Core/Engine/TorrentEngine.cs`** asks MonoTorrent to
   resolve its real metadata (name, file list, size) over the ordinary BitTorrent wire protocol
   (BEP 9 `ut_metadata`), using whatever peers MonoTorrent's own DHT lookup finds for that hash.
   Many hashes will time out — that's expected; it just means nothing is currently reachable for
   that swarm.
3. Anything resolved is written to a local SQLite database with an FTS5 full-text index
   (**`P2PShare.Core/Search/SearchIndex.cs`**). The Search tab queries this database. Nothing about
   a search ever leaves your machine.
4. Picking a result adds its magnet link to the real download engine, which fetches it from the
   swarm over DHT-discovered peers like any BitTorrent client.

## Project layout

```
src/
  P2PShare.Core/              Engine library, no UI dependency
    Dht/DhtCrawler.cs         Mainline DHT node + passive info-hash observation
    Dht/NodeId.cs             160-bit DHT node/info-hash type
    Engine/TorrentEngine.cs   MonoTorrent wrapper: metadata fetch, download, seed, torrent creation
    Search/SearchIndex.cs     SQLite + FTS5 local search index
    Settings/AppSettings.cs   JSON-persisted user settings
    P2PShareService.cs        Wires the crawler -> metadata resolver -> search index together
  P2PShare.Desktop/            Majorsilence.Forms UI (desktop today; see "Other platforms" below)
    MainForm.cs                Shell: menu, status bar, tab strip
    Tabs/SearchTab.cs           Query box + results list + download/copy-magnet
    Tabs/DownloadsTab.cs        Active transfers: progress, speed, pause/resume/remove
    Tabs/LibraryTab.cs          Folders/files you're sharing (seeding)
    Tabs/NetworkTab.cs          Crawler stats + live activity log
    Tabs/SettingsTab.cs         Download folder, bandwidth limits, ports
```

## Running it

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet run --project src/P2PShare.Desktop
```

Everything (settings, the search database, torrent fast-resume/cache data) lives under
`~/.p2pshare/`. Downloaded and shared files default to `~/P2PShare Downloads/`, changeable in
Settings.

## Other platforms ("progressive desktop/mobile")

The UI code in `P2PShare.Desktop` is plain Majorsilence.Forms — the same `Form`/`Control` code runs
on whichever backend hosts it. This build only wires up the desktop (Avalonia) head, since that's
what this environment could install and test. To add the other heads the framework supports:

```bash
dotnet new install Majorsilence.Forms.Templates
dotnet new majorsilenceforms --IncludeAndroid --IncludeWasm --IncludeiOS
```

...and point the generated Android/WASM/iOS head projects at `P2PShare.Core` and the `Tabs/*`
classes instead of writing new UI. Per the framework's own compatibility notes (as of this build):
WebAssembly is the most mature non-desktop target ("Working"), Android is "Growing" (boots, touch
works, keyboard/rotation less exercised), iOS is "Early" (compiles, not yet run on a device).

## A note on this build's environment

This was built in a network-sandboxed cloud container whose firewall only allows a fixed allowlist
of named hosts (for `apt`, NuGet, GitHub, etc.) — it has no path for raw UDP/TCP to arbitrary,
unpredictable IP addresses, which is exactly what DHT and BitTorrent peer traffic need (you can't
"allowlist" peers you haven't discovered yet). So:

- **Verified working live, in this environment:** package restore and build, the whole UI (every
  tab, screenshotted end-to-end), torrent creation and local seeding (real SHA-1 piece hashing,
  real MonoTorrent state transitions to `Seeding`), the SQLite/FTS5 search index, and graceful
  handling of unreachable info-hashes (timeouts return cleanly, nothing crashes).
- **Not reachable from this environment, by its network design, not a bug in the app:** actually
  bootstrapping onto the public DHT, observing real `get_peers`/`announce_peer` traffic, and
  fetching metadata or pieces from real internet peers. Run it on a normal machine (your desktop,
  not a locked-down cloud sandbox) and that part lights up — a BitTorrent client is simply the
  wrong kind of thing to run somewhere that can't make arbitrary outbound connections.

## Legal note

This is general-purpose BitTorrent/DHT software — the same dual-use nature as any BitTorrent client
(qBittorrent, Transmission) or open-source DHT indexer (magnetico). It doesn't fetch content from
anywhere except the peers that happen to be sharing a given info-hash, and it doesn't distinguish
infringing from non-infringing content any more than a web browser distinguishes legal from illegal
web pages. What you search for, download, and share with it is your responsibility — follow
copyright law in your jurisdiction.
