# Jellyfin-QuickSTRM

<p align="center">
  <a href="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/actions/workflows/release.yml"><img src="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/actions/workflows/release.yml/badge.svg" alt="Build Status"></a>
  <a href="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/releases"><img src="https://img.shields.io/github/v/release/Bizarrelego/Jellyfin-QuickSTRM?color=blue&label=Release" alt="Latest Release"></a>
  <a href="https://jellyfin.org/"><img src="https://img.shields.io/badge/Jellyfin-10.11%2B-purple.svg" alt="Jellyfin 10.11+"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-9.0-blueviolet.svg" alt=".NET 9.0"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Bizarrelego/Jellyfin-QuickSTRM" alt="License"></a>
</p>

<p align="center">
  <strong>High-performance, resource-conscious STRM playback acceleration and metadata probing plugin for Jellyfin.</strong>
</p>

---

## ⚡ Overview

**QuickSTRM** is an enterprise-grade playback acceleration plugin for Jellyfin media servers streaming content via `.strm` files (e.g., cloud mounts, WebDAV, AList, OpenList, Rclone, CloudDrive2, and direct HTTP/HLS streams).

Standard Jellyfin installations execute synchronous, remote `ffprobe` calls on every `.strm` playback request. Over remote connections or rate-limited cloud storage providers, this causes 5 to 30-second client buffering stalls, spinner freezes, and playback timeouts.

QuickSTRM eliminates this bottleneck by decorating Jellyfin's `IMediaSourceManager` in the dependency injection container, routing requests through an optimized three-way decision pipeline:

1. **Non-`.strm` Media**: 100% synchronous passthrough with zero state-machine overhead.
2. **Pre-Probed `.strm` Media (`RunTimeTicks > 0`)**: Automatically overrides `allowMediaProbe = false`, completely skipping remote `ffprobe` operations for **instant, sub-second playback start**.
3. **Unprobed First-Play `.strm` Media**: Executes a lightweight in-memory probe bounded by a strict 5-second cap. Codec profiles, levels, container formats, and bitrates are mapped directly in memory, and background persistence is dispatched asynchronously without blocking playback.

---

## 🎯 Architecture & Performance on Low-Spec Hardware

QuickSTRM is engineered specifically to run reliably on resource-constrained host servers (e.g., dual-core or quad-core Intel Core i3 systems, NAS appliances, and low-power microservers).

```text
[ Client Playback Request ]
           │
           ▼
[ QuickSTRM MediaSourceInterceptor ]
           │
 ┌─────────┴─────────────────────────────────────────────┐
 │                                                       │
 ▼ Is a .strm file                                       ▼ Local / Non-.strm
Check Stream Info in Database                            100% Passthrough
 │
 ├──▶ Stream Info Present (RunTimeTicks > 0)
 │    └──▶ allowMediaProbe = false ──▶ 🚀 Instant Playback Start (Zero ffprobe)
 │
 └──▶ Missing Stream Info (First Play)
      └──▶ In-Memory Probe (5s hard cap, 2-concurrency gate)
           ├── Probed OK ──▶ Populate MediaSource in memory ──▶ Playback Starts
           │               └── Dispatch Background Persistence (Async DB Save)
           └── Timeout / Starved ──▶ Fallback to raw .strm URL ──▶ Playback Starts
```

- **Strict Concurrency Gate**: Global semaphore limiting concurrent `ffprobe` operations across the server to **exactly 2**. Bulk imports, automatic file-add probes, and inline playback probes share this gate, preventing CPU thread starvation and high load spikes.
- **Dual Timeout Thresholds**: Generous 60-second timeout for background metadata scans; strict 5-second hard cap for inline playback probing to eliminate client timeouts on connected streaming devices (Fire TV Stick 4K, Apple TV, Android TV).
- **Process Lifecycle Guarantees**: Cancellation tokens are linked directly to `IMediaEncoder` and the underlying OS process tree, guaranteeing zombie `ffprobe` processes are promptly killed upon timeout.

---

## 🚀 Advanced Resilience & Queue Management

### 1. Bounded 500-Item Background Persistence Queue
When an unprobed `.strm` file is played, QuickSTRM generates in-memory stream metadata so playback starts immediately, then schedules an asynchronous task to persist the metadata into the Jellyfin SQLite/PostgreSQL database for future sessions.
- **500-Item Hard Cap**: The queue strictly caps pending persistence tasks at 500 items (`MaxPendingPersist = 500`). This prevents unbounded memory allocation and buffer bloat under heavy multi-client usage or automated script activity.
- **Thread-Safe Deduplication**: Backed by `ConcurrentDictionary<Guid, byte>`, identical item IDs are de-duplicated atomically. If a user or automated client rapidly clicks an item multiple times, redundant probe requests are instantly dropped.

### 2. 1-Hour Absolute Negative Failure Cache
Repeatedly probing dead or rate-limited remote URLs wastefully locks the 2-slot execution gate with 60-second timeouts, starving healthy tasks.
- **Negative Caching**: When a background probe fails or times out, the item ID is cached in `IMemoryCache` as a known failure.
- **Absolute Expiration (1 Hour)**: The cache strictly enforces an absolute 1-hour expiration window. Unlike sliding expirations, automated client retries or polling requests cannot continuously reset the timer and cause perpetual lockouts.
- **Fast-Reject**: Items present in the negative cache are rejected before any background task, closure, or thread pool worker is allocated.

### 3. "Reset Failure Cache" Dashboard Control
If broken remote links, expired WebDAV credentials, or upstream cloud rate limits are corrected before the 1-hour negative cache expires, administrators can manually flush the cache directly from the Jellyfin web interface.

**How to Use:**
1. Open the Jellyfin Administrator Dashboard.
2. Under **Plugins**, select **QuickSTRM**.
3. In the metrics panel, locate the **Background Queue** tile.
4. Click the **Reset Failure Cache** button.
5. The negative cache is instantly cleared via `POST /QuickStrm/Queue/Reset`, allowing previously failed items to be re-probed and persisted immediately upon the next playback or batch run.

---

## 📦 Installation & Setup

### Option 1: Custom Plugin Repository (Recommended - 1-Click Install & Auto-Updates)

QuickSTRM can be installed and kept up-to-date automatically using Jellyfin's custom repository catalog:

1. Copy the raw URL of [`manifest.json`](manifest.json) (either hosted via GitHub Pages or raw GitHub URL):
   ```text
   https://raw.githubusercontent.com/Bizarrelego/Jellyfin-QuickSTRM/main/manifest.json
   ```
2. In your Jellyfin web interface, navigate to **Dashboard -> Plugins -> Repositories**.
3. Click the **+** (Add) button.
4. Set Repository Name to **QuickSTRM** and paste the manifest URL into the **Repository URL** field.
5. Click **Save**.
6. Switch to the **Catalog** tab under Plugins. Locate **QuickSTRM** and click **Install**.
7. Restart your Jellyfin server. Future updates will now be flagged and installed automatically through the Jellyfin UI!

---

### Option 2: Manual Release Installation

1. Download `QuickSTRM.zip` or `QuickSTRM.dll` from the [Releases](https://github.com/Bizarrelego/Jellyfin-QuickSTRM/releases) page.
2. In your Jellyfin plugins directory, create a folder named `QuickSTRM`.
3. Copy `QuickSTRM.dll`, `meta.json`, and `build.yaml` into that folder.

| Deployment Type | Target Plugin Directory |
| :--- | :--- |
| **Docker (Official / LinuxServer.io)** | `/config/plugins/QuickSTRM/QuickSTRM.dll` |
| **Linux (Bare-Metal Debian/Ubuntu)** | `/var/lib/jellyfin/plugins/QuickSTRM/QuickSTRM.dll` |
| **Windows Service (Default)** | `C:\ProgramData\Jellyfin\Server\plugins\QuickSTRM\QuickSTRM.dll` |
| **Windows Portable** | `%LOCALAPPDATA%\jellyfin\plugins\QuickSTRM\QuickSTRM.dll` |

4. Restart the Jellyfin server.
5. Navigate to **Dashboard -> Plugins** and verify **QuickSTRM** is listed as Active with GUID `1278dbd6-8399-46c0-8e64-31f2d9e022ae`.

---

### Option 3: Build From Source

**Prerequisites:** [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

```bash
# Clone the repository
git clone https://github.com/Bizarrelego/Jellyfin-QuickSTRM.git
cd Jellyfin-QuickSTRM

# Clean and build release package
dotnet clean QuickSTRM.csproj -c Release
dotnet restore QuickSTRM.csproj
dotnet build QuickSTRM.csproj -c Release --no-restore
dotnet publish QuickSTRM.csproj -c Release --no-build -o ./publish/QuickSTRM
```

The compiled binary is located at `./publish/QuickSTRM/QuickSTRM.dll`.

---

## 🖥️ Dashboard Features

Access the QuickSTRM configuration page from **Dashboard -> Plugins -> QuickSTRM**:

- **Real-Time Library Metrics**:
  - **.strm Media Count**: Total `.strm` video items indexed in the Jellyfin library.
  - **Has Stream Info**: Items with stream metadata in the database (instant start enabled).
  - **Missing Stream Info**: Unprobed items requiring metadata extraction.
  - **Background Queue**: Real-time counter of items waiting for or executing background persistence.
- **Pre-Probe Engine**:
  - **Incremental Probe**: Scans and probes only items currently missing stream info.
  - **Full Probe**: Refreshes stream information and container metadata for all library `.strm` files.
  - **Live Progress**: Monitored task state (`Idle`, `Running`, `Stopping`), live progress bar, and active file name.
- **Automation Settings**:
  - **Auto-probe new .strm items on library add**: Listens to `ILibraryManager.ItemAdded` events to automatically queue new files as they appear in library folders.

---

## 🔌 REST API Reference

All endpoints require Jellyfin administrator privileges (`RequiresElevation` policy):

| Method | Endpoint | Description |
| :--- | :--- | :--- |
| `GET` | `/QuickStrm/Summary` | Returns total, probed, and unprobed `.strm` counts in the library. |
| `GET` | `/QuickStrm/Queue` | Returns background persistence queue status (`Waiting`, `Running`, `Total`). |
| `POST` | `/QuickStrm/Queue/Reset` | Flushes the 1-hour negative cache of failed background persistence items. |
| `GET` | `/QuickStrm/Probe/Status` | Returns current batch probe state (`Idle`, `Running`, `Stopping`), progress, and active item. |
| `POST` | `/QuickStrm/Probe/Start?mode={Incremental\|Full}` | Initiates a batch pre-probe task. |
| `POST` | `/QuickStrm/Probe/Stop` | Signals the active batch pre-probe task to stop gracefully. |

---

## 📄 License

This project is licensed under the Apache License 2.0. See the [LICENSE](LICENSE) file for details.
