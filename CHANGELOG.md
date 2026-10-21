# Changelog

All notable changes to the QuickSTRM project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---
## [3.0.2] - 2026-10-25

Auto-updating of manifest versions on build.

## [3.0.1] - 2026-10-20

### 🚀 Cutting-Edge Concurrency Coalescing, Zero-Allocation Pre-Warming & Remux Safeguards

Major stability and performance release delivering zero-allocation buffer pooling, rear `moov` atom pre-warming, adjacent subtitle caching, in-flight probe coalescing, multi-hop redirect resolution, and debounced telemetry I/O.

### Added
- **Zero-Allocation Buffer Pooling (`ArrayPool<byte>`)**:
  - Replaced `new byte[81920]` buffer allocations in `VfsPrewarmService` with `ArrayPool<byte>.Shared.Rent(81920)` within `try/finally` blocks returning buffers to the shared pool, eliminating LOH allocations and Gen0 garbage collection spikes during high-throughput playback sweeps.
- **Dual-End MP4/MOV Pre-Warming (Rear `moov` Atom Priming)**:
  - Local Files: For `.mp4` and `.mov` media with size > 6MB, seeks to `fileStream.Length - 1MB` and reads the final 1MB chunk to prime the VFS tail cache for container indexes placed at the end of the file.
  - Remote HTTP Streams: For `.mp4` and `.mov` URLs (or `video/mp4` content type) with size > 6MB, dispatches a secondary lightweight `GET` request with `Range: bytes=-1048576` (suffix byte range) to pre-warm the remote `moov` atom.
- **Adjacent Subtitle Caching**:
  - Automatically locates external subtitle files (`{itemFileNameWithoutExtension}*.srt`, `*.ass`, `*.ssa`, `*.vtt`) in the parent directory of `.strm` media and asynchronously reads the first 64KB of each file to prime the OS and VFS directory cache.
- **In-Flight Probe Coalescing**:
  - Introduced `_inflightProbes` (`ConcurrentDictionary<Guid, Task<bool>>`) in `ProbeService` across `ProbeOneInlineAsync` and `ProbeItemCoreAsync`, allowing concurrent playback requests for the same media item to await the existing probe task rather than spawning duplicate `ffprobe` processes or redundant hold-open loops.
- **Multi-Hop Redirect Resolution (Up to 5 Hops)**:
  - Refactored `ResolveHttpRedirectWithHoldOpenAsync` to follow HTTP 301, 302, 307, and 308 redirects up to `MaxRedirectHops = 5`.
  - Safely resolves relative `Location` headers against the current request `Uri` (`new Uri(currentUri, response.Headers.Location)`).
  - Protected against infinite redirect loops using `HashSet<string>(StringComparer.OrdinalIgnoreCase)`.
- **Automated Cache Eviction & Memory Hygiene**:
  - Added background maintenance loop running every 10 minutes via `PeriodicTimer` in `MaintenanceLoopAsync`, automatically sweeping expired keys from `_resolvedUrlCache` and `_failedItemIds` (and `_failedPersistCache`) to guarantee strictly bounded memory usage on 60,000+ item libraries.
- **Debounced Telemetry Disk I/O & Manual Management Controls**:
  - Added a 3-second `Timer` debounce latch in `TelemetryService` for `Plugin.Instance.SaveConfiguration()`, batching rapid action ledger writes during bulk scans and eliminating XML serialization disk spam.
  - Added `ClearLedger()` method to `TelemetryService` and `PluginConfiguration`.
  - Introduced authenticated REST API endpoints in `QuickStrmController`: `POST /QuickStrm/Ledger/Clear` and `POST /QuickStrm/CircuitBreaker/Reset` (secured with `[Authorize(Policy = "RequiresElevation")]`).
  - Added `CircuitBreakerOpen = _probeService.IsCircuitTripped` in `GET /QuickStrm/Queue`.
- **Dashboard UI Sanitization Pass & Polish**:
  - Comprehensive `escapeHtml(str)` sanitization across all user-supplied parameters (`Name`, `BaseUrl`, `PathFrom`, `PathTo`, ledger entries) before injection into `innerHTML`.
  - Replaced raw `&rarr;` strings with Unicode arrow `\u2192` (`→`), and display `"Path Mapping: Direct (Shared Mount)"` when paths are empty.
  - Added top-level `#circuitBreakerBanner` with inline `"Reset Circuit Breaker"` button wired to `POST /QuickStrm/CircuitBreaker/Reset`.
  - Added API key masking toggle (`type="password"` with inline Show/Hide toggle button).
  - Added `"Clear History"` button (`#btnClearLedger`) next to `#btnRefreshLedger`.

### Changed
- **Universal Route Matching & Remux Lockup Prevention**:
  - Updated `PredictivePrewarmMiddleware` route regex to `^/(?:Users/[^/]+/)?Items/([0-9a-fA-F]{8}-?(?:[0-9a-fA-F]{4}-?){3}[0-9a-fA-F]{12})$` and restricted triggers strictly to `HttpMethods.IsGet`.
  - Added `Lavf/60.3.100` User-Agent header to redirect checks, matching Jellyfin's FFmpeg process.
  - In `MediaSourceInterceptor`, immediately returns cached `MediaSourceInfo` when audio and video streams exist, ensuring remux readiness (`SupportsDirectPlay = true`, `SupportsDirectStream = true`, `SupportsTranscoding = true`) without triggering concurrent inline ffprobes during Moonfin/Astra audio remuxing.

---

## [3.0.0] - 2026-10-20

### ⚡ Native Telemetry, Predictive VFS Pre-Warming & Hardened Probe Pipeline

Major release delivering full predictive cloud pre-warming, automated proactive dead link sweeping, transparent redirect resolution, hardened probe killswitch safeguards, and native Jellyfin activity telemetry for zero-touch cloud `.strm` orchestration.

### Added
- **Predictive VFS Pre-Warmer Middleware**:
  - Implemented ASP.NET Core middleware (`PredictivePrewarmMiddleware`) intercepting item detail page loads (`^/Users/[^/]+/Items/([^/]+)$`) before client playback requests.
  - Automatically dispatches non-blocking background pre-warming via `VfsPrewarmService`, reading the initial 5MB via HTTP Range requests or FileStream reads.
  - Pages cloud VFS chunks (e.g., rclone/InfiniDysk) into RAM and primes network sockets, completely bypassing disk spin-up latency when the user hits "Play".
  - Protected with a 5-minute sliding `MemoryCache` debouncer to eliminate redundant I/O during back-and-forth UI navigation.
- **Proactive STRM Dead Link Sweeper Task**:
  - Added `ProactiveSweeperTask` implementing Jellyfin's native `IScheduledTask` interface, scheduled to run daily at 3:00 AM.
  - Sequentially scans all `.strm` media across the library using a paced verification loop (1000ms delay between items) with lightweight HTTP HEAD requests.
  - Automatically triggers the 404 auto-healing and *arr replacement workflow ahead of time, ensuring dead cloud links are replaced before users ever attempt playback.
  - Live progress reporting displayed natively in the Jellyfin Dashboard Task Manager.
- **Absolute URL Pre-Resolution**:
  - Enhanced `ReadStrmUrlAsync` with single-hop redirect pre-resolution using a dedicated `SocketsHttpHandler` with `AllowAutoRedirect = false`.
  - Automatically intercepts HTTP 301, 302, 307, and 308 redirect status codes and extracts the absolute direct-to-IP URL from the `Location` header.
  - Caches direct URLs in a thread-safe `ConcurrentDictionary`, allowing Fire TV Stick 4K and Astra clients to bypass playback redirect hops entirely.
- **Native Action Ledger UI**:
  - Added an in-memory, rolling `Queue<string> ActionLedger` capped at 100 entries in `PluginConfiguration.cs`, safely serialized via an XML-safe surrogate (`ActionLedgerSerialized`).
  - Added a dedicated REST API endpoint `GET /QuickStrm/Ledger` returning timestamped operational history.
  - Built a scrolling, styled `<div class="paperList">` ("Recent Actions") in the dashboard (`configPage.html` & `configPage.js`) with manual refresh controls and event-specific color coding for live monitoring of auto-heal and circuit breaker activity.
- **Hardened 8-Second FFprobe Killswitch**:
  - Wrapped metadata probing execution in `ProbeItemCoreAsync` and inline encoder probes in `ProbeOneInlineAsync` with a strict `CancellationTokenSource(TimeSpan.FromSeconds(8))`.
  - Catches `OperationCanceledException` to treat dead or unresponsive remote links as unreachable, immediately aborting database writes and trickplay locks.
  - Enters failed items into the 1-hour negative cache to gracefully report missing stream info and eliminate infinite-spinning client playback loops.

### Changed
- **.NET 10.0 / Jellyfin 12.2.0 SDK Migration**:
  - Upgraded target framework to `net10.0` aligned with Jellyfin 12.2.0 runtime and `12.2.0.0` Target ABI.
  - Maintained full binary compatibility with Jellyfin 12.2.0 core libraries (`MediaBrowser.Controller`, `MediaBrowser.Model`, `MediaBrowser.Common`).
- **Transition to Native `IActivityManager` Telemetry**:
  - Created `TelemetryService` injected with Jellyfin's core `MediaBrowser.Model.Activity.IActivityManager`.
  - Native activity events (`STRM Auto-Heal`, `Circuit Breaker Tripped`, `Circuit Breaker Reset`) are dispatched directly into Jellyfin's administrative Activity Log.
- **Strict `System.Text.Json` Webhook Parsing**:
  - Enforced strongly-typed `ArrWebhookPayload` POCO parsing with `[JsonPropertyName]` mappings, eliminating loose dynamic JSON inspection, reducing memory allocations, and rejecting malformed payloads with `400 Bad Request`.

### Security
- **Trickplay Database Lock Prevention**:
  - Automatically enforces `EnableTrickplay = false` and strips `.bif` metadata attachments on all probed `.strm` media, persisting the protection lock via `_itemRepository.UpdateItem`.
  - Prevents automated scheduled trickplay tasks from downloading full remote video files from cloud mounts, saving upstream bandwidth and preventing host disk exhaustion.
- **SQLite Write-Lock Debouncing**:
  - Batches file system monitor notifications and injects 50ms pacing between SQLite writes, eliminating write-lock contention (`database is locked`) during bulk season grabs on low-power host CPUs (e.g., Intel Core i3).

---

## [2.0.0] - 2026-10-20

### 🚀 Major Platform Upgrade & Zero-Touch Orchestration Suite

Major release establishing QuickSTRM as a self-contained, zero-touch `.strm` orchestration, playback acceleration, and maintenance suite natively targeting **Jellyfin 12.2.0** on **.NET 10.0**. This release completely eliminates host-level bash scripts, container wrappers, and manual webhook registration in favor of pure in-process automation, hardware safeguards, and enterprise-grade resilience.

### Added
- **Jellyfin 12.2.0 ABI & .NET 10 Migration**:
  - Upgraded target framework to `net10.0` aligned with the Jellyfin 12.2 server runtime.
  - Set plugin Target ABI to `12.2.0.0` for full plugin loader compatibility.
  - Updated GitHub Actions CI/CD release workflow to compile with .NET 10 SDK (`10.0.x`).
  - Integrated FFmpeg 8.1 media handling, preserving Intel QuickSync (QSV) hardware acceleration paths for H.264 video and AAC/AC3 audio on Fire TV Stick 4K and Astra streaming clients.
- **Native Responsive Dashboard & Live Connection Testing**:
  - Built an elevated, responsive management dashboard embedded in the Jellyfin web UI with a custom Material Design `bolt` icon.
  - Added full **Instance Edit Workflow**: Introduced `.btnEditInstance` for each configured instance, allowing administrators to modify credentials and paths in-place with smooth viewport scrolling and dynamic `"Update Connection"` / `"Save Connection"` mode switching.
  - Implemented non-mutating live connection testing (`POST /QuickStrm/Arr/TestConnection`) via a dedicated "Test Connection" button (`#btnTestArrConnection`) that tests currently populated form inputs without persisting or registering webhooks.
  - Added real-time DOM status badges (`Synced` in green, `Unsynced` in red) reflecting the active synchronization state of each configured instance.
  - Integrated real-time telemetry metrics: Circuit State, Cooldown Timer, Queue Depth (High/Low lanes), and Active Probes.
- **Zero-Touch \*arr Suite Orchestration**:
  - Automated webhook provisioning and synchronization across Radarr, Sonarr, and Whisparr without external configuration files or host scripts.
  - Background exponential backoff retry loop (5 attempts: 10s, 20s, 40s, 80s, 160s) to handle container startup race conditions when Jellyfin boots before \*arr instances.
  - Synchronous manual override (`SyncSingleInstanceAsync` / `/QuickStrm/Arr/TestAndSync`) allowing instant manual re-syncing without UI locking.
  - One-click zero-footprint webhook teardown (`POST /QuickStrm/Arr/WipeWebhooks`) with native asynchronous `Dashboard.confirm` dialogs to cleanly purge all QuickSTRM-registered notifications upon removal.
- **Autonomous "Auto-Heal on 404" Pipeline**:
  - Automatic dead link detection intercepting HTTP 404 errors and broken remote streams during playback or pre-flight checks.
  - Ancestor mount marker validation (e.g., `.mount_ok`) preventing catastrophic mass deletions if an upstream cloud mount (rclone, Google Drive, WebDAV) disconnects.
  - Reverse path translation (`PathTo` → `PathFrom`) resolving local Jellyfin file paths to external \*arr paths.
  - Automated deletion of dead file records via \*arr REST APIs paired with immediate automated replacement grab commands (`MoviesSearch`, `EpisodeSearch`, `SeriesSearch`).
- **Low-Power Hardware Safeguards & Playback Acceleration**:
  - **Debounced SQLite I/O with 50ms Pacing**: Batches filesystem monitor updates and injects 50ms pacing between SQLite writes, eliminating write-lock contention (`database is locked`) during bulk season grabs on low-power hosts (e.g., Intel Core i3).
  - **Deferred Virtual Chapter Synthesis**: Virtual 5-minute chapter synthesis is decoupled from initial playback and deferred to the `PlaybackStopped` session event, ensuring zero database write-locks while Fire TV Stick 4K or Astra clients request initial video chunks.
  - **Next-Episode Pre-Warming**: Predictively resolves and enqueues the next sequential episode in the High Priority lane upon playback start for buffer-free, instantaneous autoplay transitions.
  - **Predictive VFS Pre-Warming Middleware**: Intercepts item detail page requests (`^/Users/[^/]+/Items/([^/]+)$`) via `PredictivePrewarmMiddleware` and non-blockingly pre-warms the first 5MB via HTTP Range or FileStream read, backed by a 5-minute sliding `MemoryCache` to prevent duplicate reads on rapid client back-and-forth navigation.
  - **Proactive STRM Dead Link Sweeper Task**: Added `ProactiveSweeperTask` implementing `IScheduledTask` running daily at 3:00 AM with paced HTTP HEAD verification (1000ms delay between items) to automatically trigger 404 auto-healing and *arr replacement grabs before playback is attempted.
  - **Trickplay Prevention on .STRM Streams**: Automatically locks `EnableTrickplay = false` and purges `.bif` metadata attachments inside `ProbeItemCoreAsync`, persisting the protection lock via `_itemRepository.UpdateItem` to protect bandwidth, avoid remote bans, and eliminate host I/O churn.
  - **Global 2-Slot Concurrency Gate**: Bounded `SemaphoreSlim(2, 2)` limiting concurrent `ffprobe` processes across playback and batch scans, preventing CPU starvation.
- **Strict Webhook JSON Schema Models**:
  - Created strongly-typed `ArrWebhookPayload` POCOs with `[JsonPropertyName]` mappings, eliminating `dynamic`, `JObject`, and DOM walks to reduce memory allocations and GC pressure.
  - Nested strongly-typed models: `MovieInfo` (`FolderPath`, `FilePath`), `SeriesInfo` (`Path`), `EpisodeInfo` (`Path`, `SourcePath`), and `FileInfo` (`Path`, `RelativePath`).

### Changed
- **Webhook Endpoint Signature & Routing**: Updated `POST /QuickStrm/ArrWebhook` to accept `[FromBody] ArrWebhookPayload? payload`.
- **Strict JSON Payload Validation**: Replaced loose JSON parsing with immediate validation returning `400 Bad Request` if the payload or `EventType` is null or whitespace.
- **Switch-Based Path Extraction**: Implemented switch dispatch on `payload.EventType`, safely extracting file paths from `Movie.FilePath` (Radarr/Whisparr) or iterating `Episodes` checking `Path` and `SourcePath` (Sonarr).
- **Graceful Unsupported Event Handling**: Unsupported or test event types (`Test`, `Grab`) cleanly return `200 OK` without enqueuing, ensuring \*arr test notifications succeed without triggering spurious errors.
- **Refactored Session Monitoring**: Updated `SessionMonitorService` to consume Jellyfin 10.11/12.2 `PlaybackProgressEventArgs` event signatures and inject `IChapterManager` for chapter persistence.
- **HTTP Client & Input Sanitization**:
  - Hardened backend HTTP requests with `cleanBaseUrl = config.BaseUrl.TrimEnd('/')`, explicit `Accept: application/json` headers, and strict URL construction `${cleanBaseUrl}/api/v3/system/status`.
  - Added frontend trailing slash sanitization (`baseUrl = value.replace(/\/+$/, '')`) reassigned directly to the DOM input before testing or saving.
  - Eliminated custom modal fallbacks in favor of Jellyfin's native `Dashboard.confirm` promises with `event.preventDefault()` and `event.stopPropagation()`.

### Security
- **Query String Security Token**: Secured incoming webhook endpoints (`/QuickStrm/ArrWebhook?token={WebhookSecret}`) with an auto-generated per-installation cryptographic GUID, preventing unauthorized webhook injections.
- **Role-Based Elevation Protection**: Enforced `[Authorize(Policy = "RequiresElevation")]` across all administrative endpoints, including `POST /QuickStrm/Arr/TestConnection`, `POST /QuickStrm/Arr/TestAndSync`, and `POST /QuickStrm/Arr/WipeWebhooks`.
- **Isolated Non-Mutating Connection Testing**: `TestConnectionAsync` queries external \*arr status endpoints in memory without writing configuration to disk or triggering premature webhook registration.

### Removed
- **Purged Legacy Scripts**: Permanently removed the entire `scripts/` directory, including `jellyfin-notify.sh`, `strm-ffprobe-wrapper.sh`, and `bypass-ffprobe.sh`.
- **Purged Legacy Documentation**: Deleted `ARR-JELLYFIN-AUTOMATION.md` to prevent configuration confusion with the new native dashboard workflow.
- **Eliminated Obsolete Dependencies**: Removed manual container entrypoint hooks and host-level cron requirements.

---

## [1.1.0] - 2026-10-19

### ⚡ Zero-Touch *arr Orchestration, 404 Auto-Healing & Hardware Safeguards

Major release transforming QuickSTRM into a zero-touch .strm management, playback acceleration, and orchestration suite running 100% natively inside Jellyfin without host-level scripting.

#### Zero-Touch *arr Suite Integration
- **Automated Webhook Provisioning**: Automatically provisions and synchronizes webhooks inside Radarr, Sonarr, and Whisparr with dedicated security query tokens (`?token=...`).
- **Startup Exponential Backoff Retry Loop**: Resilient 5-attempt retry loop (`10s`, `20s`, `40s`, `80s`, `160s`) handles Docker container boot race conditions where Jellyfin starts before *arr instances.
- **Zero-Footprint Webhook Teardown**: One-click "Wipe All *arr Webhooks" cleanly purges all QuickSTRM-created webhooks from external services upon uninstall.
- **Synchronous Override**: Segregated `SyncSingleInstanceAsync` manual test & sync prevents UI locking during failed manual synchronizations.

#### Autonomous "Auto-Heal on 404" Pipeline
- **Dead Link Detection**: Intercepts deterministic HTTP 404 errors and dead cloud mount links.
- **Mount Marker Verification**: Validates cloud mount health (e.g. `.mount_ok`) before initiating deletions to protect against unmounted cloud drives.
- **Reverse Path Translation**: Automatically maps local Jellyfin paths (`PathTo`) to external *arr schemes (`PathFrom`).
- **Automated Deletion & Replacement Grab**: Removes dead movie/episode records via *arr APIs and immediately dispatches search commands (`EpisodeSearch`, `SeriesSearch`, `MoviesSearch`) across Usenet and Torrent downloaders.

#### Low-Power Hardware & Client Optimizations
- **Debounced SQLite I/O with 50ms Pacing**: Batches file system monitor notifications and injects 50ms pacing between SQLite writes, eliminating write-lock contention during bulk season downloads on low-power hosts (e.g., Intel Core i3).
- **Deferred Virtual Chapter Synthesis**: Virtual 5-minute chapter generation is deferred to the `PlaybackStopped` session event, ensuring zero database write-locks while Fire TV Stick 4K / Astra clients request initial stream chunks.
- **Next-Episode Pre-Warming**: Predictively enqueues the next sequential episode in the High Priority probe lane during playback start, guaranteeing instant, buffer-free autoplay transitions.

#### Dashboard UI & Legacy Cleanup
- **Native Sidebar Menu Elevation**: Elevated QuickSTRM to the main Jellyfin sidebar menu with native Material Design `bolt` icon.
- **Dynamic Instance Management**: Added real-time DOM status badges (`Synced` / `Unsynced/Error`), connection modal, and test/sync actions using vanilla ES6 and injected `ApiClient` / `Dashboard` globals.
- **Purged Legacy Scripts**: Deleted obsolete host-level bash scripts (`jellyfin-notify.sh`, bypass wrappers, etc.) in favor of pure in-process automation.

## [1.0.1] - 2026-10-19

### 🔧 Stability, Auto-Recovery & Compatibility Updates

- **Circuit Breaker Auto-Recovery**: Implemented a 5-minute auto-recovery cooldown with an atomic Half-Open trial state requiring end-to-end ffprobe validation before closing the breaker.
- **Trial Request Isolation**: Innocent requests arriving while a trial is in flight or cooling down exit immediately without polluting the 1-hour negative cache or incrementing failure counters.
- **Dead Link Auto-Deletion with Mount Guard**: Added optional automatic deletion for deterministic 404 dead links and missing local files, backed by ancestor mount marker checks to protect against disconnected cloud mounts.
- **Jellyfin 10.11 ActivityLog Compatibility**: Updated activity notifications to strictly use the parameterized `ActivityLog("QuickSTRM", "System", Guid.Empty)` constructor without deprecated `Severity`.
- **Release Workflow Hardening**: Updated GitHub Actions release workflow with recursive directory packaging, runtime assembly leak validation, and literal changelog extraction.

## [1.0.0] - 2026-10-19

### 🚀 Initial Standalone Release (QuickSTRM)

Initial baseline release of **QuickSTRM**, a high-performance, resource-conscious STRM playback acceleration and metadata probing plugin for Jellyfin 10.11+ running on .NET 9.0.

#### Core Architecture Rewrite
- **Standalone Fork & Unique Identity**: Forked into a distinct standalone project under namespace `QuickSTRM` with a new unique plugin GUID (`1278dbd6-8399-46c0-8e64-31f2d9e022ae`), preventing conflicts with legacy installations.
- **`IMediaSourceManager` Decorator**: Replaced intrusive HTTP filter pipelines with a clean dependency-injection decorator pattern wrapping Jellyfin's native media source manager.
- **Dual-Path Probing Architecture**:
  - **Inline In-Memory Fast Path**: Unprobed `.strm` items execute a lightweight in-memory probe bounded by a strict 5-second total cap. Directly maps codec profiles, levels, container formats, and bitrates without invoking heavy metadata refreshes or database writes.
  - **Decoupled Asynchronous Background Persistence**: Fire-and-forget background worker saves probe results to the database post-playback, ensuring subsequent plays hit the zero-probe instant-start path (`allowMediaProbe = false`).
  - **Graceful Fallback**: If an inline probe times out or encounters queue starvation, playback immediately falls back to the raw `.strm` URL, allowing client hardware (e.g., Fire TV Stick 4K) to resolve the stream natively without playback aborts.

#### Hardware & Resource Optimizations (Intel Core i3 / Low-Spec Hosts)
- **Strict 2-Process Concurrency Gate**: Implemented a global `SemaphoreSlim(2, 2)` limiting concurrent `ffprobe` operations to exactly 2 across bulk library scans, auto-probe tasks, and inline playback requests, safeguarding low-thread hosts from CPU starvation.
- **500-Item Bounded Persistence Queue**: Capped pending background persistence requests at 500 items to eliminate unbounded memory allocation and buffer bloat under high concurrency.
- **Atomic Queue Deduplication**: Backed by `ConcurrentDictionary<Guid, byte>`, duplicate play attempts and automated rapid re-requests for the same item are atomically dropped to protect memory.
- **Zombie Process Elimination**: Direct token binding with `IMediaEncoder` guarantees underlying OS `ffprobe` processes are explicitly killed upon timeout or client disconnection, preventing orphaned zombie processes.

#### Resilience & Dead Link Protection
- **1-Hour Absolute Negative Failure Cache**: Integrated `IMemoryCache` to track failed background probes for dead or rate-limited remote URLs. Known dead links are rejected immediately before allocating Tasks or worker threads.
- **Anti-Lockout Expiration Design**: Negative entries enforce an absolute 1-hour expiration rather than a sliding window, preventing automated client retries from triggering perpetual lockouts.

#### UI & REST API Additions
- **Enhanced Jellyfin Admin Dashboard**:
  - Added real-time **Background Queue** metric card displaying active waiting and running persistence counts.
  - Added **Reset Failure Cache** button for instant administrative cache clearing when remote providers recover.
  - Live pre-probe progress monitor with task status, percentage bar, and current item readout.
  - Support for Incremental and Full library-wide batch probing, plus automatic probing on newly added items.
- **RESTful Administration API (`QuickStrmController`)**:
  - `GET /QuickStrm/Summary`: Returns library `.strm` media statistics (total, probed, unprobed).
  - `GET /QuickStrm/Queue`: Returns background persistence queue metrics.
  - `POST /QuickStrm/Queue/Reset`: Flushes the 1-hour negative persistence failure cache.
  - `GET /QuickStrm/Probe/Status`: Reports current batch probe progress and active file.
  - `POST /QuickStrm/Probe/Start`: Triggers Incremental or Full library batch probe tasks.
  - `POST /QuickStrm/Probe/Stop`: Gracefully stops the active batch probe task.

#### CI/CD & Deployment Pipeline
- **Automated GitHub Actions Workflow**: Configured `.github/workflows/release.yml` for automated tag builds (`v1.*`) on .NET 9.0 SDK, incorporating strict runtime binary leak checks to prevent server assembly bloat.
- **Custom Jellyfin Repository Support**: Added root `manifest.json` enabling one-click installation and automatic future updates through Jellyfin's custom plugin repository manager.
