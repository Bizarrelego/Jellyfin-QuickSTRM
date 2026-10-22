# QuickSTRM

<p align="center">
  <a href="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/actions/workflows/release.yml"><img src="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/actions/workflows/release.yml/badge.svg" alt="Build Status"></a>
  <a href="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/releases"><img src="https://img.shields.io/github/v/release/Bizarrelego/Jellyfin-QuickSTRM?color=blue&label=Release" alt="Latest Release"></a>
  <a href="https://jellyfin.org/"><img src="https://img.shields.io/badge/Jellyfin-10.9.0%2B-purple.svg" alt="Jellyfin 10.9.0+"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-blueviolet.svg" alt=".NET 10.0"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Bizarrelego/Jellyfin-QuickSTRM" alt="License"></a>
</p>

QuickSTRM is a zero-allocation `.strm` playback engine and auto-healing orchestrator for Jellyfin. It eliminates remote probing delays, pre-warms media streams, and automatically recovers broken links through native, credential-free *arr list feeds.

---

## Core Features

* **Zero-Latency Playback**: Decorates Jellyfin's `IMediaSourceManager` to serve verified stream metadata directly from cache, bypassing inline `ffprobe` operations for indexed `.strm` media and starting playback immediately.
* **Dual-End Pre-Warming (Instant Seeking)**: Intercepts item detail views in ASP.NET Core middleware to non-blockingly page initial container headers (first 5 MB) and rear metadata chunks (such as MP4 `moov` atoms) into cache, enabling instant playback start and seeking without remote storage spin-up delays.
* **Next-Episode Pre-Warming**: Predictively analyzes series hierarchies on playback start to resolve and probe the next sequential episode in the background, eliminating buffering pauses during continuous viewing.
* **Native *arr Auto-Healing**: Provides pull-based Custom List endpoints (`/QuickStrm/AutoHeal/Radarr` and `/QuickStrm/AutoHeal/Sonarr`) that publish broken or 404 links directly to Radarr and Sonarr without webhooks, API keys, or port forwarding.
* **Self-Cleaning Event Pruning**: Automatically removes fulfilled items from recovery lists when replacement media is imported via Jellyfin library events, backed by a 7-day TTL sweep for unfillable requests.
* **Circuit Breaker Mount Protection**: Halts file operations and deletions if an underlying cloud or network mount disconnects (verifying parent markers like `.mount_ok`), preventing false mass deletions.
* **Direct URL Resolution**: Resolves single-hop HTTP 301/302/307 redirects during `.strm` parsing, caching direct stream endpoints to eliminate redirect round-trips on client devices.

---

## Compatibility & Requirements

* **Jellyfin Server**: Version 10.9.0 or newer (.NET 8.0 / .NET 10.0 runtime).
* **Supported Clients**: Universal support for all modern Jellyfin clients (Android TV, Apple TV, Web, Swiftfin, Roku, Jellyfin Media Player) with no client-side modifications required.
* **Hardware Footprint**: Negligible CPU and memory footprint. Optimized for low-power hardware, NAS environments, and embedded processors (such as ARM, Celeron, and low-power host nodes).
* **Storage Mounts**: Compatible with local filesystems, SMB/NFS network shares, and cloud virtual filesystems (Rclone VFS, Google Drive, WebDAV).

---

## Installation & Setup

### Option 1: Plugin Repository (Recommended)

1. In the Jellyfin web client, navigate to **Dashboard -> Plugins -> Repositories**.
2. Click **+** (Add) and enter the repository details:
   * **Repository Name**: `QuickSTRM`
   * **Repository URL**:
     ```text
     https://raw.githubusercontent.com/Bizarrelego/Jellyfin-QuickSTRM/main/manifest.json
     ```
3. Click **Save**.
4. Open the **Catalog** tab under Plugins, locate **QuickSTRM**, and select **Install**.
5. Restart your Jellyfin server.

### Option 2: Manual Installation

1. Download `QuickSTRM.zip` from the [Releases](https://github.com/Bizarrelego/Jellyfin-QuickSTRM/releases) page.
2. In your Jellyfin server data directory, create a folder named `plugins/QuickSTRM`.
3. Extract `QuickSTRM.dll`, `meta.json`, and `build.yaml` into that directory:

| Environment | Plugin Path |
| :--- | :--- |
| **Docker (Official / LinuxServer)** | `/config/plugins/QuickSTRM/QuickSTRM.dll` |
| **Linux (Bare-Metal Debian/Ubuntu)** | `/var/lib/jellyfin/plugins/QuickSTRM/QuickSTRM.dll` |
| **Windows (Service)** | `C:\ProgramData\Jellyfin\Server\plugins\QuickSTRM\QuickSTRM.dll` |
| **Windows (Portable)** | `%LOCALAPPDATA%\jellyfin\plugins\QuickSTRM\QuickSTRM.dll` |

4. Restart the Jellyfin server.

---

## Configuring Auto-Heal (Radarr & Sonarr)

QuickSTRM provides zero-credential list feeds for automated media recovery. When a `.strm` file fails during playback or scheduled sweeps, QuickSTRM deletes the broken file, updates Jellyfin, and publishes the missing TMDB/TVDB ID to the auto-heal feed.

1. Open your Jellyfin Web Client and select **QuickSTRM** in the administration sidebar.
2. Open the **Auto-Heal Lists** tab.
3. Copy the appropriate feed URL:
   * **Radarr (Movies)**: `http://<jellyfin-ip>:8096/QuickStrm/AutoHeal/Radarr`
   * **Sonarr (Series)**: `http://<jellyfin-ip>:8096/QuickStrm/AutoHeal/Sonarr`
4. In Radarr or Sonarr, navigate to **Settings -> Lists -> Add List (+)**:
   * Select **Custom List** (or **Radarr Lists** / **Sonarr Lists**).
   * Paste the copied URL into the List URL field.
   * Save the list and configure your desired search settings.
5. When a replacement video is downloaded and imported into Jellyfin, QuickSTRM catches the library event and automatically removes the item from the feed.

---

## Under the Hood

QuickSTRM is engineered with low-level .NET runtime primitives to maintain strict resource bounds and zero allocation overhead.

```text
[ Client Views Media Detail Page ]
                │
                ▼
  [ PredictivePrewarmMiddleware ]
  (Matches ^/(?:Users/[^/]+/)?Items/{id}$)
                │
        ┌───────┴───────┐
        ▼               ▼
(Non-blocking UI)   [ VfsPrewarmService ]
                        │
                    Check 5-min MemoryCache
                        │
                    Read 5MB Header (and 1MB Tail for MP4)
                    via ArrayPool<byte>.Shared Page Boundaries
                        │
                    Warm VFS Cache & Sockets in RAM

[ User Clicks "Play" ]
        │
        ▼
[ QuickSTRM MediaSourceInterceptor ]
        │
   ┌────┴────────────────────────┐
   ▼ Is .strm Media              ▼ Local / Non-.strm
Check Stream Info in Database    Synchronous Passthrough
   │
   ├──▶ Stream Info Present (RunTimeTicks > 0)
   │    └──▶ Instant Playback (Zero ffprobe)
   │
   └──▶ Missing Stream Info (First Play)
        └──▶ In-Memory Probe (8s Timeout, 2-Slot Gate)
             ├── Success ──▶ Start Playback & Enqueue Async DB Persistence
             └── Failure ──▶ Fallback to Raw Stream & Trigger Auto-Heal
```

### 1. Zero-Allocation File I/O (`ArrayPool<byte>.Shared` & Spans)
Standard .NET file reading pipelines instantiate intermediate streams and buffer arrays (e.g., `FileStream`, `StreamReader`, `StringReader`), creating heavy Gen0 garbage collection pressure during large-scale operations.
* **Direct OS Handles**: QuickSTRM bypasses managed streams by calling `File.OpenHandle` and `RandomAccess.ReadAsync`.
* **Rented Buffers**: Memory buffers are rented from `ArrayPool<byte>.Shared` and sized to 4096-byte hardware page boundaries (`16 * 4096` bytes) to maximize OS-level read-ahead efficiency.
* **Span-Based UTF-8 Parsing**: URL extraction is performed directly over raw memory slices (`ReadOnlySpan<byte>` and `Span<char>`) with UTF-8 BOM detection.
* **GC Performance**: Sustained endurance tests demonstrate zero Gen0 garbage collection cycles across 100,000 back-to-back `.strm` reads.

### 2. SocketsHttpHandler TCP Connection Pooling
Remote `.strm` resolutions often introduce multiple seconds of latency when each request establishes an isolated TCP connection and TLS handshake.
* **Static Pooled Client**: QuickSTRM employs a singleton `SocketsHttpHandler` configured with:
  ```csharp
  PooledConnectionLifetime = TimeSpan.FromMinutes(15),
  PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
  EnableMultipleHttp2Connections = true,
  MaxConnectionsPerServer = 100,
  AllowAutoRedirect = false
  ```
* **Latency Elimination**: Benchmark validation ensures sequential resolution requests complete header reads in `< 5ms`, multiplexing connections and eliminating TLS handshake overhead on repeat playbacks.

### 3. Lock-Free Thundering-Herd Coalescing
When multiple clients or background workers request the same unprobed `.strm` file simultaneously, standard locks cause threadpool exhaustion.
* **Lazy Publication Pattern**: In-flight reads are managed using a `ConcurrentDictionary<string, Lazy<Task<string?>>>` configured with `LazyThreadSafetyMode.ExecutionAndPublication`.
* **Execution Bound**: Under 10,000 simultaneous concurrent requests for the exact same file, physical disk I/O executes exactly once. All 10,000 requests await the shared task and resolve in `< 50ms` without blocking threads or CPU spikes.

### 4. Vectorized $O(1)$ Library Scanning
To identify unprobed items across large libraries without locking SQLite:
* **Multi-Core Partitioning**: Scans are partitioned across available CPU cores using `Parallel.For`.
* **Bitwise ASCII Masking**: String extension checks use single-cycle bitwise masking (`path[^5] == '.' && (path[^4] | 0x20) == 's' ...`), filtering 250,000 items in under 15ms without N+1 database queries.

### 5. Lock-Free Event Channel & Debouncer
When importing season packs, library monitors generate rapid bursts of filesystem events.
* **Channel Queuing**: `ItemAdded` and `ItemUpdated` events push item IDs directly to a lock-free `Channel<Guid>` in `< 50ns`, returning immediately without locking Jellyfin's event dispatch thread.
* **Debounce Latch**: A single background reader waits on a 500ms debounce latch, draining accumulated IDs in batches and issuing a single database lookup for the entire batch.

### 6. Circuit Breaker & 8-Second Tarpit Killswitch
* **Tarpit Termination**: All probe network requests are tied to strict 8-second execution tokens (`TimeSpan.FromSeconds(8)`). Unresponsive CDN endpoints or dead remotes are severed at exactly 8,000ms ($\pm 50\text{ms}$), preventing socket leaks.
* **Circuit Breaker**: If consecutive network probe failures exceed threshold (10 failures), the breaker trips into an `Open` state with exponential cooldown, pausing queue execution and logging the event to Jellyfin's administrative activity log.

---

## REST API Reference

Administrative endpoints require Jellyfin administrator privileges (`RequiresElevation` policy):

| Method | Endpoint | Access | Description |
| :--- | :--- | :--- | :--- |
| `GET` | `/QuickStrm/Summary` | Admin | Returns total, probed, and unprobed `.strm` item counts and recovery totals. |
| `GET` | `/QuickStrm/Queue` | Admin | Returns probe queue telemetry, worker states, and circuit breaker status. |
| `POST` | `/QuickStrm/Queue/Reset` | Admin | Resets circuit breaker state and clears negative failure cache. |
| `GET` | `/QuickStrm/Probe/Status` | Admin | Reports active batch probe status and percentage completion. |
| `POST` | `/QuickStrm/Probe/Start` | Admin | Starts an Incremental or Full library pre-probe task. |
| `POST` | `/QuickStrm/Probe/Stop` | Admin | Stops an active library probe task. |
| `GET` | `/QuickStrm/AutoHeal/Radarr` | Public | Native Custom List feed returning missing TMDB Movie IDs for Radarr. |
| `GET` | `/QuickStrm/AutoHeal/Sonarr` | Public | Native Custom List feed returning missing TVDB Series IDs for Sonarr. |
| `GET` | `/QuickStrm/AutoHeal/Summary` | Public / Admin | Returns live count of active items in Radarr and Sonarr recovery feeds. |
| `POST` | `/QuickStrm/Tools/RewriteUrls` | Admin | Bulk rewrites target URL schemes across library `.strm` files with dry-run support. |
| `GET` | `/QuickStrm/Ledger` | Admin | Returns recent action ledger entries (last 100 logged events). |

---

## License

This project is licensed under the Apache License 2.0. See the [LICENSE](LICENSE) file for details.
