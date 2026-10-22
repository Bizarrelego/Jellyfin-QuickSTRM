# Jellyfin-QuickSTRM

<p align="center">
  <a href="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/actions/workflows/release.yml"><img src="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/actions/workflows/release.yml/badge.svg" alt="Build Status"></a>
  <a href="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/releases"><img src="https://img.shields.io/github/v/release/Bizarrelego/Jellyfin-QuickSTRM?color=blue&label=Release" alt="Latest Release"></a>
  <a href="https://jellyfin.org/"><img src="https://img.shields.io/badge/Jellyfin-12.2%2B-purple.svg" alt="Jellyfin 12.2+"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-blueviolet.svg" alt=".NET 10.0"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Bizarrelego/Jellyfin-QuickSTRM" alt="License"></a>
</p>

<p align="center">
  <strong>QuickSTRM v4.1.0 — Zero-Overhead Playback Acceleration, Native Self-Cleaning Auto-Heal List Feeds, and Autonomous Maintenance for Cloud-Mounted .STRM Libraries on Jellyfin 12.2+.</strong>
</p>

---

## ⚡ Overview & Core Pitch

**QuickSTRM v4.1.0** is the definitive zero-touch `.strm` orchestration suite, playback acceleration engine, and automated maintenance system engineered natively for **Jellyfin 12.2+** on **.NET 10.0**.

Managing cloud-mounted media libraries (such as Rclone, InfiniDysk, Google Drive, or WebDAV mounts) traditionally introduces crippling latency, dropped streams, and constant administrative intervention. Remote media links expire or 404, cloud mount spin-up times leave clients hanging on indefinite loading spinners, background trickplay extractors thrash network bandwidth, and legacy webhook orchestrations introduce brittle API key dependencies.

**QuickSTRM delivers complete, in-process automation natively inside Jellyfin:**

* 🚀 **Instant Sub-Second Playback**: Decorates Jellyfin's `IMediaSourceManager` in the dependency injection container, bypassing remote `ffprobe` operations for already-probed `.strm` media.
* ⚡ **Predictive VFS Pre-Warming**: Intercepts item detail page loads in ASP.NET Core middleware (`^/(?:Users/[^/]+/)?Items/([0-9a-fA-F-]+)$`) to non-blockingly page the first 5MB of video into RAM, priming network sockets and eliminating cloud storage spin-up latency before the user presses "Play".
* 🌿 **Zero-Overhead Native Auto-Heal List Feeds**: Replaces bloated \*arr REST API polling and webhook synchronization with high-efficiency, pull-based native Custom List feeds (`/QuickStrm/AutoHeal/Radarr` and `/QuickStrm/AutoHeal/Sonarr`). Zero API keys, zero base URLs, and zero port-forwarding required.
* 🧹 **Event-Driven Pruning & 7-Day TTL Eviction**: Subscribes directly to Jellyfin's `ItemAdded` and `ItemUpdated` events to immediately evict fulfilled items from the recovery feeds when grabbed, paired with an automated 7-day TTL sweep so impossible-to-find releases never bloat list ingestion.
* 🩺 **Autonomous 404 Auto-Healing**: Deterministically intercepts broken or removed cloud links during playback, pre-flight checks, or scheduled sweeps, purges the dead `.strm` file from disk, refreshes the Jellyfin parent folder for immediate UI removal, and pushes the TMDB/TVDB provider ID to the native recovery feed.
* 🧹 **Proactive Dead Link Sweeper**: Native daily scheduled task running at 3:00 AM with paced HTTP HEAD verification (1000ms delay between items) to automatically trigger 404 auto-healing before playback is attempted.
* 📺 **Next-Episode Pre-Warming**: Predictively resolves the next sequential episode in the High Priority lane upon playback start, eliminating buffer stalls on streaming devices (Fire TV Stick 4K, Astra, Apple TV) during continuous binge-watching.
* 🌐 **Absolute URL Pre-Resolution**: Automatically executes lightweight HTTP HEAD checks with `AllowAutoRedirect = false` when reading `.strm` files, caching direct-to-IP URLs from 301/302/307 redirects to bypass playback redirect hops on streaming clients.
* 🛑 **Hardened 8-Second FFprobe Killswitch**: Strict 8-second execution timeout wrapping all probe operations; unresolvable links are aborted and negative-cached for 1 hour to prevent client UI infinite-spinning.
* 📜 **Native Activity Logging & UI Action Ledger**: Injected with Jellyfin's `IActivityManager` to log auto-heal and circuit breaker events directly to the Jellyfin Activity Log, backed by an in-memory 100-entry scrolling ledger on the dashboard.
* 🚫 **Automated Trickplay Lock Protection**: Automatically locks `.strm` media with `EnableTrickplay = false` and strips `.bif` metadata attachments, preventing background tasks from downloading full remote video files.
* 🛡️ **Hardware-Tailored Safeguards**: Specifically engineered for resource-constrained host servers (e.g., dual/quad-core Intel Core i3 systems, low-power NAS devices) to prevent SQLite write-lock contention and CPU thread starvation.

---

## 🛠️ Hardware Optimizations (Low-Power Hosts & Streaming Clients)

QuickSTRM is meticulously tailored for hardware-constrained host environments (such as an **Intel Core i3** server, Celeron/N-series NAS, or mini-PC) delivering direct-play streams to budget client devices (like an **Amazon Fire TV Stick 4K** running the **Astra** client). These optimizations ensure buttery-smooth playback with zero host lockups:

### 1. Predictive VFS Header Paging (Bypassing InfiniDysk / Cloud Spin-Up Latency)
Cloud-mounted virtual filesystems (such as InfiniDysk or Rclone VFS) often take 3 to 10 seconds to spin up connections, fetch remote file chunks, and open initial read streams. When a user taps a movie in their client, this latency creates long loading spinners or player timeouts.
* **Middleware Interception**: As soon as a user opens an item's detail page, `PredictivePrewarmMiddleware` intercepts the HTTP request (`^/Users/[^/]+/Items/([^/]+)$`) and passes it down the pipeline instantly without blocking the UI.
* **Background 5MB Read**: Dispatches a background task via `VfsPrewarmService` that reads the first 5MB of the video via an HTTP Range request or local FileStream.
* **RAM Priming**: Pages initial container headers into host memory, primes DNS and TCP/TLS sockets, and warms the VFS cache so when the user clicks "Play", playback starts **instantaneously** on the Fire TV Stick 4K Astra client without hammering the Core i3 CPU.
* **5-Minute Debounce Cache**: Backed by a sliding `MemoryCache` so rapid browsing back and forth never triggers redundant network fetches.

### 2. Absolute URL Pre-Resolution (Eliminating Redirect Hops on Fire TV)
Remote media providers frequently deliver `.strm` files containing shortened or vanity URLs that redirect via HTTP 301, 302, or 307 to direct-to-IP media streams. Lower-power streaming devices (such as Fire TV Stick 4K running Astra) struggle with multiple connection handshakes and SSL negotiation hops during playback initiation.
* **Lightweight HEAD Pre-Resolution**: In `ReadStrmUrlAsync`, QuickSTRM executes a 3-second HTTP HEAD request with `AllowAutoRedirect = false`.
* **Direct Target Extraction**: Extracts the direct absolute URL from the `Location` header and stores it in a fast, in-memory `ConcurrentDictionary`.
* **Direct Handshake**: Subsequent playback requests and pre-warm passes receive the resolved direct-to-IP URL, bypassing redirect hops completely.

### 3. Hardened 8-Second FFprobe Killswitch
Stalled cloud mounts or dead remote CDN nodes can cause standard Jellyfin metadata probes to hang indefinitely, consuming background CPU threads and causing client streaming interfaces to spin forever.
* **Strict 8-Second Timeout**: Every remote metadata probe is tied to a strict 8-second token (`using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));`).
* **Clean Abort**: If remote probes exceed 8 seconds, the probe is aborted, database writes are canceled, and the item is entered into the 1-hour negative cache.
* **Zero Client Hanging**: The player immediately handles missing stream info gracefully rather than locking the client UI in an infinite loading state.

### 4. Debounced SQLite Write-Lock Pacing (50ms Inter-Write Delay)
When Sonarr or Radarr grabs a multi-season series or bulk release, external webhooks can flood Jellyfin with dozens of simultaneous notifications. Calling `ILibraryMonitor.ReportFileSystemChanged` concurrently for each file hammers SQLite, generating `database is locked` exceptions and stalling active streams.
* **Concurrent Hash Set Collection**: Incoming webhook notifications are deduplicated in memory.
* **3-Second Stabilization Debounce Window**: Rapid-fire file events are held until disk activity settles.
* **50ms Inter-Write Pacing**: When committing batches to SQLite, QuickSTRM injects a dedicated 50ms delay between every `ReportFileSystemChanged` dispatch. This allows the host CPU (e.g., Intel Core i3) and SQLite engine to finalize write transactions cleanly without choking active direct-play streams.

### 5. Deferred Virtual Chapter Synthesis (Playback-Safe)
Generating virtual 5-minute chapter markers on `.strm` media enables users to seek and skip effortlessly without heavy chapter extraction. However, executing `Item.SaveChapters()` during stream initialization locks the SQLite database at the exact millisecond a Fire TV Stick 4K or Astra client requests initial media chunks—causing playback stutter, buffering loops, or timeout errors.
* **Deferred Execution**: QuickSTRM decouples chapter synthesis from initial playback and defers it until the `PlaybackStopped` session event fires.
* **Zero Disk Contention**: Database and disk I/O remain completely idle during stream startup, ensuring butter-smooth playback initiation on Astra and Fire TV Stick 4K endpoints.

### 6. FFmpeg 8.1 & Intel QuickSync (QSV) Direct Mapping
Jellyfin 12.2 integrates FFmpeg 8.1. QuickSTRM preserves native hardware acceleration pathways (Intel QuickSync Video), prioritizing direct stream copy and hardware transcoding to H.264 video and AAC/AC3 audio for optimal compatibility on Fire TV Stick 4K devices.

### 7. Global 2-Slot Concurrency Gate & Dual-Lane Queue
QuickSTRM enforces a server-wide execution gate capped at **exactly 2 concurrent `ffprobe` operations**:
* **High Priority Lane**: Playback start and incoming \*arr webhook events bypass background work for instant resolution.
* **Low Priority Lane**: Library-wide batch pre-probes and routine maintenance run in the background without starving host CPU threads.

### 8. Circuit Breaker with Mount Guard (`.mount_ok`)
If an upstream cloud mount (Rclone, Google Drive, WebDAV) drops, probing hundreds of missing files will trigger false deletions and hammer dead remotes.
* **Mount Marker Verification**: Scans up to 5 parent directories for a designated marker file (e.g., `.mount_ok`). If missing, deletions are immediately suppressed.
* **Exponential Backoff**: After consecutive probe failures, the circuit breaker trips into an `Open` state (`10 * 2^(n-1)` seconds backoff) to protect system resources.
* **Half-Open Probe Trial**: Once cooldown expires, a single trial probe is allowed through to confirm mount recovery before resuming full queue processing.

---

## 🚀 Performance & Scale (Aspirational Benchmarks)

QuickSTRM v4.1.0 introduces a rigorous benchmark-driven performance architecture verified by aspirational test suites establishing strict performance ceilings. If the codebase cannot satisfy these bounds, tests fail deterministically:

| Benchmark Directive | Metric / Ceiling | Technical Enforcement | Architecture Impact |
| :--- | :--- | :--- | :--- |
| **Zero-Allocation Memory Enforcement** | **$< 2\text{KB}$ allocations** | `File.OpenHandle`, `RandomAccess.ReadAsync`, `ArrayPool<byte>.Shared`, `ReadOnlySpan<byte>` | Completely abandons `FileStream` ($80\text{KB}/64\text{KB}$ internal buffer allocations), `StreamReader`, `StringReader`, and LINQ in favor of pooled buffers and raw span parsing. Guarantees zero Gen0 GC pressure on low-power Intel Core i3 / Celeron servers. |
| **Thundering Herd / Lock-Free Concurrency** | **$< 50\text{ms}$ await completion** | `ConcurrentDictionary<string, Lazy<Task<T>>>` (`ExecutionAndPublication`) | Simulates 10,000 simultaneous threads requesting the exact same `.strm` file at the exact same millisecond. Underlying physical I/O executes **exactly 1 time**, and all 10,000 threads await the same task in parallel without blocking locks (`lock (obj)`) or threadpool starvation. |
| **$O(1)$ Algorithm Scale Testing** | **$< 15\text{ms}$ full-library sweep** | Multi-core hardware partitioning (`Parallel.For`), bitwise ASCII masking (`(ch \| 0x20)`) | Mocks an extreme hoarder's library of 250,000 video items. Evaluates `RunTimeTicks` and dimensions in $O(1)$ and extracts all unprobed items in **~3-5ms** with zero N+1 SQLite queries or heavy object instantiations. |
| **Ruthless Socket Timeout & Tarpit Killswitch** | **$8000\text{ms} \pm 50\text{ms}$ abort** | Strict 8s `CancellationTokenSource`, immediate socket disposal | Simulates a dead upstream Debrid/InfiniDysk mount trickling 1 byte every 30 seconds. Enforces connection termination at exactly 8,000ms, disposes the socket immediately, and eliminates TCP port leakage. |

---

## 📱 Client Compatibility (Moonfin, Astra & Fire TV)

QuickSTRM provides dedicated, first-class compatibility and playback acceleration for modern Jellyfin client applications, with specialized optimizations for **Moonfin** (iOS / Apple TV / macOS) and **Astra** (Amazon Fire TV / Android TV / Google TV):

### Full Support for Moonfin and Astra
* **Universal Endpoint Route Matching**: Jellyfin 12.x and next-generation native clients like Moonfin navigate item hierarchies using both legacy user-scoped endpoints (`/Users/{userId}/Items/{itemId}`) and modern direct endpoints (`/Items/{itemId}`). QuickSTRM's `PredictivePrewarmMiddleware` utilizes a unified, pre-compiled route regex:
  ```regex
  ^/(?:Users/[^/]+/)?Items/([0-9a-fA-F]{8}-?(?:[0-9a-fA-F]{4}-?){3}[0-9a-fA-F]{12})$
  ```
  This ensures predictive VFS pre-warming triggers reliably across Moonfin, Astra, Jellyfin Media Player, and web clients alike, while strictly filtering on `GET` requests to prevent false triggers during item metadata updates.
* **FFmpeg User-Agent Impersonation (`Lavf/60.3.100`)**: Remote CDNs, debrid providers, and cloud storage gateways often handle generic HTTP clients differently than FFmpeg, occasionally rejecting standard HEAD requests or imposing distinct redirect paths. QuickSTRM dispatches redirect pre-resolution checks using the exact `User-Agent: Lavf/60.3.100` header expected by Jellyfin's transcoding and remuxing engine, ensuring the pre-resolved URL is 100% identical to what FFmpeg negotiates.
* **Bypassing Audio Remux Lockups on Constrained Hosts**: On hardware-constrained host processors (such as Intel Core i3 or Celeron nodes), spinning up an inline `ffprobe` process concurrently with Jellyfin's `ffmpeg` audio remuxing pipeline causes severe CPU thread starvation and mount deadlock. QuickSTRM's `MediaSourceInterceptor` immediately returns cached `MediaSourceInfo` whenever cached audio and video streams exist in the database, explicitly ensuring `SupportsDirectPlay = true`, `SupportsDirectStream = true`, and `SupportsTranscoding = true`. Concurrently, `ProbeService` coalesces in-flight probe tasks across concurrent requests, completely eliminating remux lockups and player freeze on Moonfin and Astra endpoints.

---

## 🎯 Architecture & Playback Flow

```text
[ Client Browses Media Detail Page (e.g., Fire TV Stick 4K / Astra Client) ]
                               │
                               ▼
        [ PredictivePrewarmMiddleware (^/Users/.../Items/{id}$) ]
                               │
                ┌──────────────┴──────────────┐
                ▼ (Non-blocking Next)         ▼ (Background Task)
        Continue to UI Screen       [ VfsPrewarmService ]
                                              │
                                    Check 5-min MemoryCache
                                              │
                                    Read 5MB via HTTP Range / FileStream
                                              │
                                    Prime VFS Chunks & Sockets in RAM

[ User Clicks "Play" ]
         │
         ▼
[ QuickSTRM MediaSourceInterceptor ]
         │
    ┌────┴────────────────────────┐
    ▼ Is .strm Media              ▼ Local / Non-.strm
Check Stream Info in Database    100% Synchronous Passthrough
    │
    ├──▶ Stream Info Present (RunTimeTicks > 0)
    │    └──▶ allowMediaProbe = false ──▶ 🚀 Instant Playback (Zero ffprobe)
    │
    └──▶ Missing Stream Info (First Play)
         └──▶ In-Memory Probe (8s killswitch, 2-concurrency slot)
              ├── Probed OK ──▶ Populate MediaSource in memory ──▶ Playback Starts
              │                 └── Dispatch Background Persistence (Async DB Save)
              └── Timeout / Failure ──▶ Fallback to Raw URL ──▶ Playback Starts
```

---

## 🔄 The Self-Cleaning Auto-Heal Pipeline

QuickSTRM bridges Jellyfin and your \*arr media managers into a zero-overhead, pull-based recovery pipeline:

```text
[ Dead Link Encountered (404 / 503 / Hold-Open Retries Exhausted) ]
                        │
                        ▼
            [ Mount Marker Validation ]
      (Ensures mount is online; prevents false purge)
                        │
                        ▼
             [ Delete Physical .strm File ]
                        │
                        ▼
        [ Refresh Parent Folder Metadata in Jellyfin ]
         (Item immediately vanishes from Jellyfin UI)
                        │
                        ▼
      [ Push TMDB / TVDB ID to AutoHealService Feed ]
                        │
                        ▼
   [ *arr Custom List Ingestion (Radarr / Sonarr) ]
    GET /QuickStrm/AutoHeal/Radarr OR /QuickStrm/AutoHeal/Sonarr
                        │
                        ▼
    [ Download Clients Grab Working Stream Replacement ]
                        │
                        ▼
   [ Fulfilled Item Imported -> Pruned from Feed via ItemAdded ]
```

1. **Deterministic Dead Link Interception**: When a `.strm` link returns a hard 404/503 during playback or scheduled scans and exhausts all hold-open retries, QuickSTRM triggers immediate auto-healing.
2. **Mount Protection Gate**: QuickSTRM verifies that the cloud mount is healthy (via `.mount_ok` marker or directory check), preventing mass deletions if a network mount disconnects.
3. **Physical File Removal & Instant UI Refresh**: QuickSTRM deletes the dead `.strm` file directly from the filesystem and instructs Jellyfin's `ILibraryManager` to refresh the parent folder so the broken item disappears from user interfaces immediately.
4. **Provider ID Feed Ingestion**: Extracts the item's TMDB ID (for Movies) or TVDB ID (for Series/Episodes) and enqueues it into `AutoHealService`.
5. **Native Custom List Ingestion**: Radarr and Sonarr poll their respective native list endpoints (`/QuickStrm/AutoHeal/Radarr` and `/QuickStrm/AutoHeal/Sonarr`) without requiring API keys, webhook provisioning, or port forwarding.
6. **Event-Driven Automatic Pruning**: When a replacement video file is grabbed and added to Jellyfin, `ILibraryManager.ItemAdded` and `ItemUpdated` automatically extract its provider IDs and purge the entry from the recovery dictionary.
7. **7-Day TTL Expiration Sweep**: Entries older than 7 days are automatically evicted during periodic background sweeps, preventing rare or unavailable releases from permanently bloating the recovery list.

---

## 📺 Next-Episode Pre-Warming

When binge-watching on streaming hardware like the **Amazon Fire TV Stick 4K** running the **Astra** client, transitioning between episodes often introduces 5–15 second buffering stalls while the device resolves remote stream info.

QuickSTRM eliminates this bottleneck:
1. When playback begins on an episode, `SessionMonitorService` catches the `PlaybackStart` event.
2. It queries the season hierarchy and identifies the next sequential episode.
3. If the next episode is a `.strm` file missing stream info, it is immediately enqueued in the **High Priority Probe Lane**.
4. By the time the current episode finishes, the subsequent episode's container, audio/video streams, and runtime ticks are already persisted in the database.
5. Autoplay transitions occur **instantaneously** with zero client buffer stalls.

---

## 🖥️ Native Dashboard & Zero-Touch Setup

QuickSTRM integrates directly into the native Jellyfin administrator dashboard and elevates to the main sidebar menu with a dedicated Material Design `bolt` icon.

### Modern Segmented 3-Tab Enterprise Dashboard

QuickSTRM v4.1.0 features a clean, high-density tabbed administrative layout:

1. **📊 Overview & Probing Tab**:
   * **Real-Time Summary Metrics**: Instant visibility into total `.strm` files, probed streams, unprobed items, and cache efficiency.
   * **Telemetry & Circuit Breaker**: Live monitoring of High/Low priority probe queues, active worker slots, negative cache depth, and circuit breaker trip states.
   * **Background Probe Controller**: Trigger non-blocking **Incremental** (unprobed items only) or **Full** (library-wide re-probe) passes with real-time percentage progress bars and cancel controls.
2. **🌿 Auto-Heal Lists Tab**:
   * **Native Custom List Feed URLs**: Dedicated endpoints for **Radarr** (`http://[server-ip]:8096/QuickStrm/AutoHeal/Radarr`) and **Sonarr** (`http://[server-ip]:8096/QuickStrm/AutoHeal/Sonarr`).
   * **One-Click URL Copying**: Instant clipboard copy buttons for effortless setup in your \*arr List settings.
   * **Live Recovery Counters**: Real-time counter pills showing exactly how many missing movies and series are currently awaiting replenishment.
   * **Zero Configuration Overhead**: No API keys, no base URLs, and no manual "Clear/Flush" buttons required (feeds are completely self-cleaning).
3. **🛠️ Maintenance & Advanced Tab**:
   * **Streaming Diagnostics & Cache Control**: Flush negative probe caches to retry previously failed streams.
   * **Bulk URL Rewrite Utility**: Safely migrate upstream CDN endpoints or credentials across all library `.strm` files in bulk with dry-run verification.
   * **Live "Recent Actions" Ledger**: Live-scrolling event log tracking auto-heals, circuit breaker transitions, and maintenance actions.

### 2-Minute Radarr / Sonarr List Setup

1. Open your Jellyfin Web Client and click **QuickSTRM** in the sidebar.
2. Switch to the **Auto-Heal Lists** tab.
3. Copy the **Radarr Feed URL** or **Sonarr Feed URL**.
4. In Radarr or Sonarr, navigate to **Settings -> Lists -> + (Add List)**:
   * Select **Custom List** (or **Radarr Lists** / **Sonarr Lists**).
   * Paste the copied URL into the List URL field.
   * Save the list and configure your desired search settings.
5. That's it! When QuickSTRM encounters a dead `.strm` link, Radarr and Sonarr automatically ingest the missing ID and trigger a replacement grab. When fulfilled, QuickSTRM prunes it immediately upon import.

---

## 📦 Installation Options

### Option 1: Custom Plugin Repository (Recommended)

1. In your Jellyfin web interface, navigate to **Dashboard -> Plugins -> Repositories**.
2. Click the **+** (Add) button.
3. Set **Repository Name** to `QuickSTRM` and paste the manifest URL:
   ```text
   https://raw.githubusercontent.com/Bizarrelego/Jellyfin-QuickSTRM/main/manifest.json
   ```
4. Click **Save**.
5. Switch to the **Catalog** tab under Plugins. Locate **QuickSTRM** and click **Install**.
6. Restart your Jellyfin server. Future updates will be flagged and installed automatically through the Jellyfin UI!

---

### Option 2: Manual Release Installation

1. Download `QuickSTRM.zip` from the [Releases](https://github.com/Bizarrelego/Jellyfin-QuickSTRM/releases) page.
2. In your Jellyfin server data directory, create a folder named `plugins/QuickSTRM`.
3. Extract `QuickSTRM.dll`, `meta.json`, and `build.yaml` into that directory.

| Deployment Type | Target Plugin Directory |
| :--- | :--- |
| **Docker (Official / LinuxServer.io)** | `/config/plugins/QuickSTRM/QuickSTRM.dll` |
| **Linux (Bare-Metal Debian/Ubuntu)** | `/var/lib/jellyfin/plugins/QuickSTRM/QuickSTRM.dll` |
| **Windows Service (Default)** | `C:\ProgramData\Jellyfin\Server\plugins\QuickSTRM\QuickSTRM.dll` |
| **Windows Portable** | `%LOCALAPPDATA%\jellyfin\plugins\QuickSTRM\QuickSTRM.dll` |

4. Restart Jellyfin.

---

### Option 3: Build From Source

**Prerequisites:** [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```bash
git clone https://github.com/Bizarrelego/Jellyfin-QuickSTRM.git
cd Jellyfin-QuickSTRM

dotnet clean QuickSTRM.csproj -c Release
dotnet restore QuickSTRM.csproj
dotnet build QuickSTRM.csproj -c Release --no-restore
dotnet publish QuickSTRM.csproj -c Release --no-build -o ./publish/QuickSTRM
```

---

## 🔌 REST API Reference

All administrative endpoints require Jellyfin administrator privileges (`RequiresElevation` policy):

| Method | Endpoint | Authorization | Description |
| :--- | :--- | :--- | :--- |
| `GET` | `/QuickStrm/Summary` | Admin | Returns total, probed, unprobed `.strm` item counts, and auto-heal feed totals. |
| `GET` | `/QuickStrm/Ledger` | Admin | Returns the recent action ledger entries (last 100 events). |
| `GET` | `/QuickStrm/Queue` | Admin | Returns telemetry metrics, lane depths, circuit state, and cooldown remaining. |
| `POST` | `/QuickStrm/Queue/Reset` | Admin | Flushes the 1-hour negative cache of failed probe items. |
| `GET` | `/QuickStrm/Probe/Status` | Admin | Reports current batch probe task progress, percentage, and active file. |
| `POST` | `/QuickStrm/Probe/Start` | Admin | Starts an Incremental or Full library pre-probe task. |
| `POST` | `/QuickStrm/Probe/Stop` | Admin | Stops an active library probe task. |
| `GET` | `/QuickStrm/AutoHeal/Radarr` | Public | Native Custom List feed returning missing TMDB Movie IDs for Radarr ingestion. |
| `GET` | `/QuickStrm/AutoHeal/Sonarr` | Public | Native Custom List feed returning missing TVDB Series IDs for Sonarr ingestion. |
| `GET` | `/QuickStrm/AutoHeal/Summary` | Public / Admin | Returns live count of active items in Radarr and Sonarr recovery feeds. |
| `POST` | `/QuickStrm/Tools/RewriteUrls` | Admin | Bulk rewrites target URL schemes across all `.strm` files (supports Dry Run preview). |

---

## 📄 License

This project is licensed under the Apache License 2.0. See the [LICENSE](LICENSE) file for details.
