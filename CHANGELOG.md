# Changelog

All notable changes to the QuickSTRM project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

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
