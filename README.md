# QuickSTRM

<p align="center">
  <a href="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/actions/workflows/release.yml"><img src="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/actions/workflows/release.yml/badge.svg" alt="Build Status"></a>
  <a href="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/releases"><img src="https://img.shields.io/github/v/release/Bizarrelego/Jellyfin-QuickSTRM?color=blue&label=Release" alt="Latest Release"></a>
  <a href="https://jellyfin.org/"><img src="https://img.shields.io/badge/Jellyfin-10.9.0%2B-purple.svg" alt="Jellyfin 10.9.0+"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-blueviolet.svg" alt=".NET 10.0"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Bizarrelego/Jellyfin-QuickSTRM" alt="License"></a>
</p>

QuickSTRM is a high-performance `.strm` playback engine, universal client streaming adapter, and auto-healing orchestrator for Jellyfin. It eliminates remote probing latencies, prevents inline probe storms, normalizes complex query-based stream containers, predictively pre-warms storage and network sockets, and autonomously recovers broken links through native, credential-free *arr list feeds.

---

## Key Features

* **Universal DirectPlay & Streaming Engine**: Automatically normalizes container formats (including query-string extensions such as `?downloadKey=...&extension=mkv`), populates stream-level bitrates, resolves `RunTimeTicks`, and configures playback flags (`SupportsDirectPlay`, `SupportsDirectStream`, `SupportsTranscoding`). Enables native DirectPlay on **Moonfin**, **Jellyfin Media Player (JMP)**, **Android TV / Fire TV (ExoPlayer)**, and **Apple TV**, while delivering seamless 18x+ HLS remuxing to Web browsers.
* **In-Memory Cache & Probe Storm Cooldown**: Thread-safe in-memory cache (`CachedProbeResult`) serves stream descriptors in $0\text{ ms}$, backed by a 30-second per-item cooldown latch that completely eliminates duplicate inline probes during concurrent playback handshakes (`master.m3u8`, `main.m3u8`, `PlaybackInfo`).
* **Predictive Dual-End VFS Pre-Warming**: Intercepts item detail page navigation to non-blockingly page the initial 512 KB container header and 1 MB rear metadata chunk (such as MP4 `moov` atoms) into cache, pre-warming cloud VFS mounts (Rclone, Google Drive, WebDAV) and adjacent subtitles (`.srt`, `.vtt`, `.ass`). Active pre-warm requests are immediately canceled on playback start to grant exclusive socket bandwidth to the player.
* **Next-Episode Pre-Warming**: Predictively identifies sequential episodes (next episode in season, or premiere of the next season) on playback start or progress, warming metadata in the background to ensure gapless transitions during binge-watching.
* **Deferred Virtual Chapter Synthesis**: Automatically synthesizes 5-minute virtual chapter markers for `.strm` media once runtime is resolved, deferring SQLite persistence until playback stops to eliminate database write-lock contention during active streaming.
* **Native *arr Auto-Healing**: Provides pull-based Custom List endpoints (`/QuickStrm/AutoHeal/Radarr` and `/QuickStrm/AutoHeal/Sonarr`) that publish broken or 404 links directly to Radarr and Sonarr without webhooks, API keys, or exposed inbound ports.
* **Self-Cleaning Event Pruning**: Listens to Jellyfin library import events via lock-free channels with a 500ms debounce latch, automatically evicting fulfilled entries from recovery feeds, complemented by a 7-day TTL sweep for stale entries.
* **Circuit Breaker & Mount Guard**: Trips after 10 consecutive network failures to pause background queues and protect remote endpoints, backed by an optional mount marker file check (`MountMarkerFileName`, e.g., `.mount_ok`) that halts file purges if a network or cloud mount drops.
* **Proactive Dead Link Sweeper**: Built-in scheduled task (3:00 AM daily) that paces HTTP verification across library `.strm` files, safely identifying 404 targets, purging dead stubs, and queuing automated *arr replacements.
* **Bulk URL / Path Rewriter**: Integrated administrative tool (`/QuickStrm/Tools/RewriteUrls`) to update domain names, IP addresses, or path prefixes across library `.strm` files with live dry-run inspection.

---

## Architecture & Lifecycle

```text
[ Client Navigates to Item Detail Page ]
                 │
                 ▼
   [ PredictivePrewarmMiddleware ]
   (Matches GET ^/(?:Users/[^/]+/)?Items/{id}$)
                 │
         ┌───────┴───────┐
         ▼               ▼
(Non-blocking UI)   [ VfsPrewarmService ]
                         │
                     Check 5-minute Sliding Cache
                         │
                     Read 512KB Header + 1MB Tail (MP4 moov) + Subtitles
                     via ArrayPool<byte>.Shared Page Boundaries
                         │
                     Warm Remote Sockets & OS/VFS Cache in RAM

[ User Clicks "Play" ]
         │
         ├──▶ (If Pre-warm Active: CancelPrewarm gives exclusive socket to player)
         │
         ▼
[ MediaSourceInterceptor (IMediaSourceProvider) ]
         │
         ├──▶ Step 1: Check In-Memory ProbeResultCache (0ms lookup)
         │           └── Hit ──▶ Populate MediaSourceInfo & Start Playback Immediately
         │
         ├──▶ Step 2: Check SQLite / Item MediaStreams
         │           └── Valid ──▶ Populate MediaSourceInfo & Start Playback Immediately
         │
         └──▶ Step 3: Unprobed Item (First Play)
                     │
                     ├── Cooldown Latch Check (Prevents duplicate probes within 30s)
                     └── In-Memory Inline Probe (8s Timeout, 2-Slot Concurrency Gate)
                          ├── Success ──▶ Cache in RAM, Populate MediaSourceInfo, Enqueue Async DB Save
                          └── Failure ──▶ Fallback to Raw Stream & Trigger Auto-Heal (if 404)

[ Active Playback & Completion ]
         │
         ├──▶ Playback Start/Progress ──▶ SessionMonitorService Pre-warms Next Episode (SxxExx+1)
         │
         └──▶ Playback Stopped ──────────▶ Deferred Virtual Chapter Synthesis (5-min intervals)
```

---

## Technical Specifications

### 1. Zero-Allocation File I/O
* **Direct OS Handles**: Uses `File.OpenHandle` and `RandomAccess.ReadAsync` to bypass managed stream allocations (`FileStream`, `StreamReader`).
* **Hardware-Aligned Buffers**: Buffers rented from `ArrayPool<byte>.Shared` sized to 4096-byte boundaries (`16 * 4096 = 64 KB`) for maximum kernel page-cache throughput.
* **Span-Based UTF-8 Parsing**: Reads target URLs directly across raw memory slices (`ReadOnlySpan<byte>` and `Span<char>`) with UTF-8 BOM detection, achieving zero Gen0 GC pressure across hundreds of thousands of reads.

### 2. High-Throughput HTTP Connection Pooling
* Singleton `SocketsHttpHandler` configured with:
  ```csharp
  PooledConnectionLifetime = TimeSpan.FromMinutes(15),
  PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
  EnableMultipleHttp2Connections = true,
  MaxConnectionsPerServer = 100,
  AllowAutoRedirect = false
  ```
* Direct single-hop redirect resolution (caching 301/302/307 targets) eliminates client redirect latency.

### 3. Thundering-Herd Concurrency Control
* In-flight probe and read operations utilize `ConcurrentDictionary<string, Lazy<Task<...>>>` with `LazyThreadSafetyMode.ExecutionAndPublication`.
* When thousands of concurrent requests target the same unprobed item during playback handshakes, physical execution occurs exactly once.

### 4. Vectorized Library Scanning
* Library sweeps use `Parallel.For` partitioned across available CPU cores.
* Employs single-cycle bitwise ASCII masking (`path[^5] == '.' && (path[^4] | 0x20) == 's' ...`), filtering 250,000+ items in $< 15\text{ ms}$ without SQLite query exhaustion.

---

## Compatibility & Requirements

* **Jellyfin Server**: Version 10.9.0 or newer (.NET 10.0 runtime).
* **Supported Clients**: Universal support across all Jellyfin clients (**Moonfin**, **Jellyfin Media Player**, **Android TV**, **Fire TV**, **Apple TV / Swiftfin**, **Roku**, **Web Client**) with zero client-side configuration.
* **Storage Mounts**: Compatible with local storage, NFS/SMB shares, and virtual filesystems (Rclone VFS, CloudDrive, Google Drive, WebDAV).
* **Hardware**: Optimized for low-power servers (Intel Celeron, Raspberry Pi / ARM64, NAS appliances).

---

## Installation

### Option 1: Plugin Repository (Recommended)

1. Open your Jellyfin Web Client and go to **Dashboard -> Plugins -> Repositories**.
2. Click **+** (Add) and enter:
   * **Repository Name**: `QuickSTRM`
   * **Repository URL**:
     ```text
     https://raw.githubusercontent.com/Bizarrelego/Jellyfin-QuickSTRM/main/manifest.json
     ```
3. Click **Save**.
4. Open the **Catalog** tab, locate **QuickSTRM**, and select **Install**.
5. Restart your Jellyfin server.

### Option 2: Manual Installation

1. Download `QuickSTRM.zip` from the [Releases](https://github.com/Bizarrelego/Jellyfin-QuickSTRM/releases) page.
2. In your Jellyfin server data directory, navigate to or create the `plugins/QuickSTRM` folder.
3. Extract `QuickSTRM.dll`, `meta.json`, and `build.yaml` into that directory:

| Environment | Plugin File Location |
| :--- | :--- |
| **Docker (Official / LinuxServer)** | `/config/plugins/QuickSTRM/QuickSTRM.dll` |
| **Linux (Bare-Metal Debian/Ubuntu)** | `/var/lib/jellyfin/plugins/QuickSTRM/QuickSTRM.dll` |
| **Windows (Service)** | `C:\ProgramData\Jellyfin\Server\plugins\QuickSTRM\QuickSTRM.dll` |
| **Windows (Portable)** | `%LOCALAPPDATA%\jellyfin\plugins\QuickSTRM\QuickSTRM.dll` |

4. Restart the Jellyfin server.

---

## Configuration Reference

Settings can be customized via the Jellyfin Web Client under **Dashboard -> Plugins -> QuickSTRM**:

| Setting | Default | Description |
| :--- | :--- | :--- |
| **Enable Auto Probe** | `false` | Automatically probes newly added `.strm` items in the background when imported into the library. |
| **Enable Auto-Heal (Delete)** | `true` | Automatically deletes dead `.strm` files (failed probe or HTTP 404) and queues them into the Radarr/Sonarr recovery feed. Suspended while the circuit breaker is tripped. |
| **Probe Mode** | `OnlyUnprobed` | Operational mode for manual scans (`OnlyUnprobed` skips previously probed media; `FullRefresh` rescans the entire library). |
| **Mount Marker File** | `""` | Name of a canary file (e.g., `.mount_ok`) searched up to 5 parent directories above a `.strm`. If defined and missing, operations are treated as dropped mounts, preventing mass deletion. |
| **Enable Next-Episode Pre-Warm** | `true` | Predictively pre-warms and probes the next sequential episode in the background when an episode starts playing. |
| **Synthesize Virtual Chapters** | `true` | Synthesizes 5-minute virtual chapter markers for `.strm` media once runtime is resolved, deferred until playback completion. |

---

## Auto-Heal Setup (Radarr & Sonarr)

QuickSTRM eliminates complex webhooks and external scripts by providing native Custom List feeds:

1. Open your Jellyfin Web Client, open the administration sidebar, and select **QuickSTRM**.
2. Select the **Auto-Heal Lists** tab to view your feed URLs:
   * **Radarr (Movies - TMDB)**: `http://<jellyfin-ip>:8096/QuickStrm/AutoHeal/Radarr`
   * **Sonarr (Series - TVDB)**: `http://<jellyfin-ip>:8096/QuickStrm/AutoHeal/Sonarr`
3. In Radarr or Sonarr, navigate to **Settings -> Lists -> Add List (+)**:
   * Select **Custom List** (or **Radarr Lists** / **Sonarr Lists**).
   * Paste the corresponding feed URL.
   * Set your desired search and monitoring behaviors.
4. When a dead `.strm` file is encountered, QuickSTRM extracts its metadata provider ID, deletes the dead stub, and publishes the ID to the feed.
5. Once your *arr application grabs and imports the replacement media into Jellyfin, QuickSTRM automatically detects the library update and evicts the entry from the list.

---

## REST API Reference

Administrative endpoints require Jellyfin administrator privileges (`RequiresElevation` policy):

| Method | Endpoint | Access | Description |
| :--- | :--- | :--- | :--- |
| `GET` | `/QuickStrm/Dashboard` | Admin | Returns unified telemetry, active probe queue states, circuit breaker status, and auto-heal items. |
| `GET` | `/QuickStrm/Telemetry` | Admin | Returns total probed, pre-warmed, healed, and deleted item counters, along with the recent action ledger. |
| `GET` | `/QuickStrm/Summary` | Admin | Returns library `.strm` counts (total, probed, unprobed) and failure cache metrics. |
| `GET` | `/QuickStrm/Queue` | Admin | Returns dual-priority queue depths, worker states, and circuit breaker status. |
| `POST` | `/QuickStrm/Queue/Reset` | Admin | Clears the negative failure cache, allowing previously failed items to be retried. |
| `POST` | `/QuickStrm/CircuitBreaker/Reset` | Admin | Manually resets the circuit breaker to the closed state. |
| `GET` | `/QuickStrm/Probe/Status` | Admin | Reports active manual probe status, progress counters, and active worker tasks. |
| `POST` | `/QuickStrm/Probe/Start` | Admin | Initiates a manual library pre-probe batch (`OnlyUnprobed` or `FullRefresh`). |
| `POST` | `/QuickStrm/Probe/Stop` | Admin | Cancels an active library pre-probe batch. |
| `GET` | `/QuickStrm/AutoHeal/Radarr` | Public | Native Custom List feed returning unfulfilled TMDB Movie IDs for Radarr. |
| `GET` | `/QuickStrm/AutoHeal/Sonarr` | Public | Native Custom List feed returning unfulfilled TVDB Series IDs for Sonarr. |
| `GET` | `/QuickStrm/AutoHeal/Summary` | Public | Returns active item counts in the Radarr and Sonarr recovery feeds. |
| `GET` | `/QuickStrm/Ledger` | Admin | Returns the 100 most recent action ledger entries. |
| `POST` | `/QuickStrm/Ledger/Clear` | Admin | Clears all entries from the action ledger history. |
| `POST` | `/QuickStrm/Tools/RewriteUrls` | Admin | Executes bulk path/prefix replacements across library `.strm` files with dry-run support. |

---

## License

This project is licensed under the Apache License 2.0. See the [LICENSE](LICENSE) file for details.
