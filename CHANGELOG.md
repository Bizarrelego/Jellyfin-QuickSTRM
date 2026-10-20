# Changelog

All notable changes to the QuickSTRM project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
