# Jellyfin-QuickSTRM

<p align="center">
  <a href="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/actions/workflows/release.yml"><img src="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/actions/workflows/release.yml/badge.svg" alt="Build Status"></a>
  <a href="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/releases"><img src="https://img.shields.io/github/v/release/Bizarrelego/Jellyfin-QuickSTRM?color=blue&label=Release" alt="Latest Release"></a>
  <a href="https://jellyfin.org/"><img src="https://img.shields.io/badge/Jellyfin-12.2%2B-purple.svg" alt="Jellyfin 12.2+"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-blueviolet.svg" alt=".NET 10.0"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Bizarrelego/Jellyfin-QuickSTRM" alt="License"></a>
</p>

<p align="center">
  <strong>Zero-Touch .STRM Playback Acceleration, *arr Suite Orchestration, and Automated Auto-Healing Suite for Jellyfin 12.2.</strong>
</p>

---

## ⚡ Overview & Core Pitch

**QuickSTRM** is an enterprise-grade, zero-touch `.strm` management and playback acceleration suite built natively for **Jellyfin 12.2+** running on **.NET 10.0**. 

Traditional `.strm` workflows require fragile host-level plumbing: external shell scripts (`jellyfin-notify.sh`), container entrypoint wrappers (`bypass-ffprobe.sh`), and manually registered webhooks. When remote links die or container stacks restart out of order, links break, clients freeze on indefinite loading spinners, and administrators are forced to debug shell scripts and Docker mounts manually.

**QuickSTRM eliminates all external dependencies and host-level scripting.** It runs 100% self-contained inside the Jellyfin process:

* **Instant Sub-Second Playback**: Decorates Jellyfin's `IMediaSourceManager` in the dependency injection container, skipping remote `ffprobe` network round-trips for already-probed `.strm` media.
* **Zero-Touch \*arr Suite Orchestration**: Automatically discovers, provisions, and configures authenticated webhooks inside **Radarr**, **Sonarr**, and **Whisparr** and cleanly tears them down with a single click upon removal.
* **Autonomous 404 Auto-Healing**: Deterministically detects dead or removed cloud streams, reverse-translates paths to the corresponding \*arr instance, deletes the dead file record, and immediately fires automated searches across Usenet and Torrent indexers for a working replacement.
* **Next-Episode Pre-Warming**: Predictively resolves the next sequential episode in the High Priority lane during playback so continuous binge-watching on streaming clients (Fire TV Stick 4K, Astra, Apple TV) starts instantaneously with zero client buffer stalls.
* **Low-Power Hardware Safeguards**: Engineered specifically to protect resource-constrained host servers (e.g., dual/quad-core Intel Core i3 systems, NAS appliances) from SQLite database write-locks and CPU starvation.

---

## 🎯 Architecture & Playback Pipeline

```text
[ Client Playback Request (e.g. Fire TV Stick 4K / Astra Client) ]
                               │
                               ▼
             [ QuickSTRM MediaSourceInterceptor ]
                               │
        ┌───────────────────────┴────────────────────────┐
        │                                               │
        ▼ Is .strm Media                                ▼ Local / Non-.strm
Check Stream Info in Database                          100% Synchronous Passthrough
        │
        ├──▶ Stream Info Present (RunTimeTicks > 0)
        │    └──▶ allowMediaProbe = false ──▶ 🚀 Instant Playback (Zero ffprobe)
        │
        └──▶ Missing Stream Info (First Play)
             └──▶ In-Memory Probe (5s cap, 2-concurrency slot)
                  ├── Probed OK ──▶ Populate MediaSource in memory ──▶ Playback Starts
                  │                 └── Dispatch Background Persistence (Async DB Save)
                  └── Timeout / Failure ──▶ Fallback to Raw URL ──▶ Playback Starts
```

1. **Non-`.strm` Media**: 100% synchronous passthrough with zero state-machine or memory allocation overhead.
2. **Pre-Probed `.strm` Media**: Sets `allowMediaProbe = false`, completely bypassing remote `ffprobe` operations for **instant, sub-second playback start**.
3. **Unprobed First-Play `.strm` Media**: Executes a lightweight in-memory probe bounded by a strict 5-second hard cap. Codec profiles, levels, container formats, and bitrates are populated directly in memory so playback begins immediately, while persistent metadata updates are scheduled in the background.

---

## 🛠️ Hardware Optimizations (Low-Power Servers & Streaming Clients)

QuickSTRM is engineered specifically for hardware-constrained host servers (such as Intel Core i3 machines, Celeron/N-series NAS devices, or low-power mini-PCs) streaming to budget client hardware (like **Amazon Fire TV Stick 4K**, **Fire TV Cube**, or Android TV devices running the **Astra** client). These safeguards ensure Jellyfin remains responsive without locking the database or stuttering active playback:

### 1. Debounced SQLite I/O with 50ms Pacing
When Sonarr or Radarr grabs a full season or bulk release, external webhooks can flood Jellyfin with dozens of simultaneous notifications. Firing `ILibraryMonitor.ReportFileSystemChanged` synchronously for each item hammers the SQLite database, causing `database is locked` exceptions and interface freezes.
* **Thread-Safe Concurrent Hash Set**: Incoming webhook file paths are atomically collected and deduplicated in memory.
* **Background Stabilization Debounce Window**: File system notifications are held for a 3-second stabilization window.
* **50ms Inter-Write Pacing**: When flushing batches to SQLite, QuickSTRM injects a dedicated 50ms delay between every `ReportFileSystemChanged` dispatch. This gives the SQLite engine time to commit transactions without blocking active media streams.

### 2. Deferred Virtual Chapter Synthesis (Playback-Safe)
Generating virtual 5-minute chapters on `.strm` media allows users to jump around files easily without heavy chapter extraction. However, executing `Item.SaveChapters()` during stream initialization locks the SQLite database at the exact millisecond a Fire TV Stick 4K or Astra player is requesting initial video chunks—causing playback stutter or timeout errors.
* **Deferred Synthesis**: Virtual chapter generation is decoupled from the playback path and deferred until the `PlaybackStopped` session event fires.
* **Zero-Interference Streaming**: Disk I/O remains completely idle during the critical playback start phase.

### 3. FFmpeg 8.1 & Intel QuickSync (QSV) Direct Mapping
Jellyfin 12.2 integrates FFmpeg 8.1. QuickSTRM ensures stream mapping directly honors hardware acceleration pathways (Intel QuickSync Video), prioritizing direct stream copy and hardware transcoding to H.264 video and AAC/AC3 audio for butter-smooth playback on Fire TV Stick 4K and Astra clients.

### 4. 2-Slot Global Concurrency Gate & Dual-Lane Queue
QuickSTRM enforces a server-wide execution semaphore capped at **exactly 2 concurrent `ffprobe` slots**:
* **High Priority Lane**: Playback start and incoming \*arr webhooks bypass background queues for instant resolution.
* **Low Priority Lane**: Library-wide batch pre-probes and routine maintenance run in the background without starving active streams.

### 5. Circuit Breaker with Exponential Backoff Mount Protection
If an upstream cloud mount (Rclone, Google Drive, WebDAV, Debrid mount) drops offline, probing hundreds of missing files will trigger false-positive deletions and hammer dead remotes.
* **Mount Marker Verification**: Traverses up to 5 parent directories looking for a designated marker file (e.g., `.mount_ok`). If missing, deletions are immediately suppressed.
* **Exponential Backoff**: After consecutive probe failures, the circuit breaker trips into an `Open` state (`10 * 2^(n-1)` seconds backoff) to protect system resources.
* **Half-Open Probe Trial**: Once cooldown expires, a single trial probe is allowed through to confirm mount recovery before resuming full queue processing.

---

## 🔄 The "Auto-Heal on 404" Pipeline

QuickSTRM bridges Jellyfin and your \*arr media managers into a closed-loop, self-healing system:

```text
[ Dead Link Encountered (HTTP 404 / Remote Drop) ]
                        │
                        ▼
            [ Mount Marker Validation ]
            (Ensures mount is online; prevents false purge)
                        │
                        ▼
       [ Path Translation (PathTo ──▶ PathFrom) ]
                        │
                        ▼
     [ Delete File Record in Radarr / Sonarr / Whisparr ]
       DELETE /api/v3/moviefile/{id} OR /api/v3/episodefile/{id}
                        │
                        ▼
        [ Automated Replacement Search Command ]
          POST /api/v3/command { name: "EpisodeSearch" / "MoviesSearch" }
                        │
                        ▼
     [ Usenet / Torrent Grabbers Fetch Working Stream ]
```

1. **Deterministic 404 Detection**: When a `.strm` pre-flight check returns an HTTP 404 or persistent unresolvable state, QuickSTRM initiates the auto-heal workflow.
2. **Mount Protection Gate**: QuickSTRM verifies that the cloud mount itself is active (via folder contents or `.mount_ok` marker), preventing mass deletions caused by unmounted rclone drives.
3. **Reverse Path Translation**: QuickSTRM matches the local Jellyfin filesystem path to the corresponding instance configuration (`PathTo` &rarr; `PathFrom`) to resolve the exact internal path recognized by Radarr/Sonarr.
4. **File Record Deletion**: QuickSTRM calls the \*arr REST API to delete the dead movie or episode file record from the database.
5. **Automated Search Trigger**: Immediately dispatches an authenticated search command (`MoviesSearch`, `EpisodeSearch`, or `SeriesSearch`) through the \*arr API. Your connected download clients (Usenet via SABnzbd/NZBGet, or Torrents via qBittorrent/Transmission) automatically grab an alternative release and replace the dead link without any user intervention.

---

## 📺 Next-Episode Pre-Warming

When binge-watching a series on hardware-constrained streaming devices like the **Amazon Fire TV Stick 4K**, **Fire TV Cube**, or **Android TV** running apps such as **Astra** or the native Jellyfin client, the transition between episodes is often plagued by 5–15 second buffering stalls or timeout crashes while the device waits for remote ffprobe resolution.

QuickSTRM eliminates this bottleneck with **Next-Episode Pre-Warming**:
1. When playback begins on any television episode, QuickSTRM's `SessionMonitorService` intercepts the `PlaybackStart` event.
2. It dynamically identifies the current episode's index, queries the season hierarchy, and locates the next sequential episode.
3. If the next episode is a `.strm` file missing stream info, QuickSTRM immediately injects it into the **High Priority Probe Lane**.
4. By the time the current episode finishes, the subsequent episode's container, audio/video streams, and runtime ticks are already persisted in the database.
5. Autoplay transitions occur **instantaneously**, providing a smooth, Netflix-like binge-watching experience on budget client devices.

---

## 🖥️ Native Dashboard & Zero-Touch Setup

QuickSTRM integrates directly into the native Jellyfin administrator dashboard and elevates to the main sidebar menu with a dedicated Material Design `bolt` icon.

### 1-Click Zero-Touch Setup

1. Open your Jellyfin Web Client and click the **QuickSTRM** tab in the native sidebar navigation menu (or navigate to **Dashboard -> QuickSTRM**).
2. Under **Zero-Touch \*arr Suite Integration**, click **+ Add Instance**.
3. Fill in your instance details:
   * **Instance Name**: e.g., `Radarr 4K` or `Sonarr HD`
   * **Application Type**: Select `Radarr`, `Sonarr`, or `Whisparr`
   * **Base URL**: e.g., `http://192.168.1.50:7878` or `http://sonarr:8989`
   * **API Key**: Found in your \*arr instance under *Settings -> General -> Security*
   * **Remote Path (\*arr)**: e.g., `/movies` or `/data/media/tv` (leave blank if identical to Jellyfin)
   * **Local Path (Jellyfin)**: e.g., `/media/movies` or `/mnt/cloud/tv` (leave blank if identical to \*arr)
   * **Auto-Heal on 404**: Checked by default to enable automatic dead link replacements.
4. Click **Test Connection** adjacent to the API Key field to perform an immediate, non-mutating live connectivity check against the \*arr API.
5. Click **Save Connection**. QuickSTRM will automatically save the configuration, connect to the instance, and provision the secured webhook notification named `QuickSTRM Auto-Sync` with an auto-generated cryptographic security query token (`?token={WebhookSecret}`).
6. The instance will display an active **Synced** status badge in green.

### Resilient Startup Retry Policy
If Jellyfin boots faster than your Radarr or Sonarr containers (common in Docker Compose environments or after Watchtower updates), QuickSTRM executes a 5-attempt background retry loop with exponential backoff (`10s`, `20s`, `40s`, `80s`, `160s`) until your \*arr instances are reachable.

### Clean Zero-Footprint Uninstall
Click **Wipe All \*arr Webhooks** at any time to cleanly scrub all QuickSTRM-managed webhooks from Radarr, Sonarr, and Whisparr with a single confirmation.

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
6. Restart your Jellyfin server. Future updates will now be flagged and installed automatically through the Jellyfin UI!

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
| `GET` | `/QuickStrm/Summary` | Admin | Returns total, probed, and unprobed `.strm` item counts. |
| `GET` | `/QuickStrm/Queue` | Admin | Returns telemetry metrics, lane depths, circuit state, and cooldown remaining. |
| `POST` | `/QuickStrm/Queue/Reset` | Admin | Flushes the 1-hour negative cache of failed probe items. |
| `GET` | `/QuickStrm/Probe/Status` | Admin | Reports current batch probe task progress, percentage, and active file. |
| `POST` | `/QuickStrm/Probe/Start` | Admin | Starts an Incremental or Full library pre-probe task. |
| `POST` | `/QuickStrm/Probe/Stop` | Admin | Stops an active library probe task. |
| `POST` | `/QuickStrm/Arr/TestConnection` | Admin | Performs an isolated, live connectivity test against a single \*arr instance without saving or provisioning. |
| `POST` | `/QuickStrm/Arr/TestAndSync` | Admin | Manually tests connections and provisions/updates webhooks across all configured \*arr instances. |
| `POST` | `/QuickStrm/Arr/WipeWebhooks` | Admin | Cleanly deletes all QuickSTRM webhooks across all configured \*arr instances for a zero-footprint uninstall. |
| `POST` | `/QuickStrm/Tools/RewriteUrls` | Admin | Bulk rewrites target URL schemes across all `.strm` files (supports Dry Run preview). |
| `POST` | `/QuickStrm/ArrWebhook` | Token (`?token=...`) | Receives incoming grab/download notifications from Radarr, Sonarr, and Whisparr with strict JSON schema validation. |

---

## 📄 License

This project is licensed under the Apache License 2.0. See the [LICENSE](LICENSE) file for details.
