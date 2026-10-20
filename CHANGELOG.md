# Changelog

All notable changes to the QuickSTRM project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
