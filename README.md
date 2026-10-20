# Jellyfin-QuickSTRM

<p align="center">
  <a href="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/actions/workflows/release.yml"><img src="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/actions/workflows/release.yml/badge.svg" alt="Build Status"></a>
  <a href="https://github.com/Bizarrelego/Jellyfin-QuickSTRM/releases"><img src="https://img.shields.io/github/v/release/Bizarrelego/Jellyfin-QuickSTRM?color=blue&label=Release" alt="Latest Release"></a>
  <a href="https://jellyfin.org/"><img src="https://img.shields.io/badge/Jellyfin-12.2%2B-purple.svg" alt="Jellyfin 12.2+"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-blueviolet.svg" alt=".NET 10.0"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Bizarrelego/Jellyfin-QuickSTRM" alt="License"></a>
</p>

<p align="center">
  <strong>Zero-Touch .STRM Orchestration Suite, Playback Acceleration, and Autonomous 404 Auto-Healing for Jellyfin 12.2.</strong>
</p>

---

## ⚡ Overview & Core Pitch

**QuickSTRM** is a complete, zero-touch `.strm` orchestration suite and playback acceleration engine built natively for **Jellyfin 12.2+** running on **.NET 10.0**.

Traditional `.strm` setups rely on fragile external bash scripts (`jellyfin-notify.sh`), container entrypoint hacks (`bypass-ffprobe.sh`), and manually configured webhooks. When remote links die, cloud mounts disconnect, or container stacks restart asynchronously, streams freeze on indefinite loading spinners, SQLite databases lock up, and administrators are left troubleshooting shell scripts and Docker mounts manually.

**QuickSTRM permanently replaces all external scripts and host-level wrappers with native, in-process automation:**

* 🚀 **Instant Sub-Second Playback**: Decorates Jellyfin's `IMediaSourceManager` in the dependency injection container, bypassing remote `ffprobe` operations for already-probed `.strm` media.
* 🤖 **Zero-Touch \*arr Suite Orchestration**: Automatically provisions, synchronizes, and configures authenticated webhooks inside **Radarr**, **Sonarr**, and **Whisparr**—and cleans them up with a single click upon removal.
* 🩺 **Autonomous 404 Auto-Healing**: Deterministically intercepts broken or removed cloud links during playback or pre-flight checks, reverse-translates paths to the corresponding \*arr instance, deletes the dead file record, and triggers an immediate automated replacement grab.
* 📺 **Next-Episode Pre-Warming**: Predictively resolves the next sequential episode in the High Priority lane upon playback start, eliminating buffer stalls on streaming devices (Fire TV Stick 4K, Astra, Apple TV) during continuous binge-watching.
* 🛡️ **Hardware-Tailored Safeguards**: Specifically engineered for resource-constrained host servers (e.g., dual/quad-core Intel Core i3 systems, low-power NAS devices) to prevent SQLite write-lock contention and CPU thread starvation.

---

## 🛠️ Hardware Optimizations (Low-Power Hosts & Streaming Clients)

QuickSTRM is meticulously tailored for hardware-constrained host environments (such as an **Intel Core i3** server, Celeron/N-series NAS, or mini-PC) delivering direct-play streams to budget client devices (like an **Amazon Fire TV Stick 4K** running the **Astra** client). These optimizations ensure buttery-smooth playback with zero host lockups:

### 1. Debounced SQLite Write-Lock Pacing (50ms Inter-Write Delay)
When Sonarr or Radarr grabs a multi-season series or bulk release, external webhooks can flood Jellyfin with dozens of simultaneous notifications. Calling `ILibraryMonitor.ReportFileSystemChanged` concurrently for each file hammers SQLite, generating `database is locked` exceptions and stalling active streams.
* **Concurrent Hash Set Collection**: Incoming webhook notifications are deduplicated in memory.
* **3-Second Stabilization Debounce Window**: Rapid-fire file events are held until disk activity settles.
* **50ms Inter-Write Pacing**: When committing batches to SQLite, QuickSTRM injects a dedicated 50ms delay between every `ReportFileSystemChanged` dispatch. This allows the host CPU (e.g., Intel Core i3) and SQLite engine to finalize write transactions cleanly without choking active direct-play streams.

### 2. Deferred Virtual Chapter Synthesis (Playback-Safe)
Generating virtual 5-minute chapter markers on `.strm` media enables users to seek and skip effortlessly without heavy chapter extraction. However, executing `Item.SaveChapters()` during stream initialization locks the SQLite database at the exact millisecond a Fire TV Stick 4K or Astra client requests initial media chunks—causing playback stutter, buffering loops, or timeout errors.
* **Deferred Execution**: QuickSTRM decouples chapter synthesis from initial playback and defers it until the `PlaybackStopped` session event fires.
* **Zero Disk Contention**: Database and disk I/O remain completely idle during stream startup, ensuring butter-smooth playback initiation on Astra and Fire TV Stick 4K endpoints.

### 3. FFmpeg 8.1 & Intel QuickSync (QSV) Direct Mapping
Jellyfin 12.2 integrates FFmpeg 8.1. QuickSTRM preserves native hardware acceleration pathways (Intel QuickSync Video), prioritizing direct stream copy and hardware transcoding to H.264 video and AAC/AC3 audio for optimal compatibility on Fire TV Stick 4K devices.

### 4. Global 2-Slot Concurrency Gate & Dual-Lane Queue
QuickSTRM enforces a server-wide execution gate capped at **exactly 2 concurrent `ffprobe` operations**:
* **High Priority Lane**: Playback start and incoming \*arr webhook events bypass background work for instant resolution.
* **Low Priority Lane**: Library-wide batch pre-probes and routine maintenance run in the background without starving host CPU threads.

### 5. Circuit Breaker with Mount Guard (`.mount_ok`)
If an upstream cloud mount (Rclone, Google Drive, WebDAV) drops, probing hundreds of missing files will trigger false deletions and hammer dead remotes.
* **Mount Marker Verification**: Scans up to 5 parent directories for a designated marker file (e.g., `.mount_ok`). If missing, deletions are immediately suppressed.
* **Exponential Backoff**: After consecutive probe failures, the circuit breaker trips into an `Open` state (`10 * 2^(n-1)` seconds backoff) to protect system resources.
* **Half-Open Probe Trial**: Once cooldown expires, a single trial probe is allowed through to confirm mount recovery before resuming full queue processing.

---

## 🎯 Architecture & Playback Flow

```text
[ Client Playback Request (e.g., Fire TV Stick 4K / Astra Client) ]
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

1. **Deterministic 404 Detection**: When a `.strm` pre-flight check returns an HTTP 404, QuickSTRM initiates the auto-heal workflow.
2. **Mount Protection Gate**: QuickSTRM verifies that the cloud mount is healthy (via folder contents or `.mount_ok` marker), preventing mass deletions caused by dropped rclone mounts.
3. **Reverse Path Translation**: QuickSTRM translates the local Jellyfin filesystem path to the remote scheme (`PathTo` &rarr; `PathFrom`) recognized by Radarr/Sonarr.
4. **File Record Deletion**: QuickSTRM calls the \*arr REST API to remove the dead movie or episode file record.
5. **Automated Search Trigger**: Immediately dispatches an authenticated search command (`MoviesSearch`, `EpisodeSearch`, or `SeriesSearch`). Connected download clients automatically grab a working release without user intervention.

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

### 1-Click Zero-Touch Setup

1. Open your Jellyfin Web Client and click the **QuickSTRM** tab in the sidebar navigation menu (or navigate to **Dashboard -> QuickSTRM**).
2. Under **Zero-Touch \*arr Suite Integration**, click **+ Add Instance**.
3. Fill in your instance details:
   * **Instance Name**: e.g., `Radarr 4K` or `Sonarr TV`
   * **Application Type**: Select `Radarr`, `Sonarr`, or `Whisparr`
   * **Base URL**: e.g., `http://192.168.1.50:7878` or `http://sonarr:8989` (trailing slashes are automatically sanitized)
   * **API Key**: Found in your \*arr instance under *Settings -> General -> Security*
   * **Remote Path (\*arr)** & **Local Path (Jellyfin)**: Leave blank if your \*arr container and Jellyfin container share the exact same volume mounts.
   * **Auto-Heal on 404**: Checked by default to enable automatic dead link replacement.
4. Click **Test Connection** adjacent to the API Key field to perform an immediate, non-mutating live connectivity check against the \*arr API.
5. Click **Save Connection**. QuickSTRM will automatically save the configuration, connect to the instance, and provision a secured webhook notification named `QuickSTRM Auto-Sync` with an auto-generated cryptographic security query token (`?token={WebhookSecret}`).
6. The instance will display an active **Synced** status badge in green.

### Editing Existing Connections
Click the **Edit** button on any configured instance card to load its parameters into the form, make changes, and click **Update Connection** to commit updates in-place.

### Resilient Startup Retry Policy
If Jellyfin boots faster than your Radarr or Sonarr containers (common in Docker Compose environments), QuickSTRM runs a 5-attempt background retry loop with exponential backoff (`10s`, `20s`, `40s`, `80s`, `160s`) until your \*arr instances are reachable.

### Clean Zero-Footprint Uninstall
Click **Wipe All \*arr Webhooks** at any time to cleanly scrub all QuickSTRM-managed webhooks from Radarr, Sonarr, and Whisparr with a single confirmation modal.

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
