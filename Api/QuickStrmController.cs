using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuickSTRM;
using QuickSTRM.Configuration;
using QuickSTRM.Models;

namespace QuickSTRM.Api;

/// <summary>
/// API endpoints for QuickSTRM probe management, live *arr webhooks, and maintenance tools.
/// </summary>
[ApiController]
[Route("QuickStrm")]
public class QuickStrmController : ControllerBase
{
    private static readonly object _debounceLock = new();
    private static ConcurrentDictionary<string, byte> _pendingFileSystemChanges = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? _debounceTimer;
    private static ILibraryMonitor? _staticLibraryMonitor;
    private static ILogger? _staticLogger;

    private readonly ProbeService _probeService;
    private readonly ArrIntegrationService _arrIntegrationService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor? _libraryMonitor;
    private readonly ILogger<QuickStrmController> _logger;
    private readonly TelemetryService _telemetryService;

    public QuickStrmController(
        ProbeService probeService,
        ArrIntegrationService arrIntegrationService,
        ILibraryManager libraryManager,
        TelemetryService telemetryService,
        ILogger<QuickStrmController> logger,
        ILibraryMonitor? libraryMonitor = null)
    {
        _probeService = probeService;
        _arrIntegrationService = arrIntegrationService;
        _libraryManager = libraryManager;
        _telemetryService = telemetryService;
        _logger = logger;
        _libraryMonitor = libraryMonitor;

        _staticLibraryMonitor = libraryMonitor ?? _staticLibraryMonitor;
        _staticLogger = logger ?? _staticLogger;
    }

    /// <summary>
    /// Returns a summary of .strm items in the library.
    /// </summary>
    [HttpGet("Summary")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetSummary()
    {
        var (total, probed, unprobed) = _probeService.GetSummary();
        return Ok(new
        {
            Total = total,
            Probed = probed,
            Unprobed = unprobed,
            CacheSize = _probeService.FailureCacheCount
        });
    }

    /// <summary>
    /// Returns recent action ledger entries.
    /// </summary>
    [HttpGet("Ledger")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetLedger()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Ok(Array.Empty<string>());
        }

        string[] entries;
        lock (config)
        {
            entries = config.ActionLedger.ToArray();
        }

        return Ok(entries);
    }

    /// <summary>
    /// Clears the recent action ledger history.
    /// </summary>
    [HttpPost("Ledger/Clear")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult ClearLedger()
    {
        _telemetryService.ClearLedger();
        return NoContent();
    }


    /// <summary>
    /// Starts a manual pre-probe task by dispatching an asynchronous background library scan.
    /// Returns 202 Accepted immediately without blocking the HTTP request thread.
    /// Mode/Scope: "Incremental" (unprobed items only) or "All" / "Full" (all .strm items).
    /// </summary>
    [HttpPost("Probe")]
    [HttpPost("Probe/Start")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult StartProbe([FromQuery] string? mode = null, [FromBody] ProbeStartRequest? body = null)
    {
        var scope = body?.Scope ?? body?.Mode ?? mode ?? "Incremental";
        var isIncremental = !string.Equals(scope, "All", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(scope, "Full", StringComparison.OrdinalIgnoreCase);

        var probeMode = isIncremental ? ProbeMode.Incremental : ProbeMode.All;
        _ = _probeService.QueueLibraryScanAsync(probeMode);

        _logger.LogInformation(
            "QuickSTRM: Manual probe initiated with scope {Scope}; background scan dispatched",
            isIncremental ? "Incremental" : "All");

        return Accepted(new
        {
            Success = true,
            ScanningLibrary = true,
            Scope = isIncremental ? "Incremental" : "All",
            Message = $"Scanning library and queueing {(isIncremental ? "Incremental" : "All")} probe items in background."
        });
    }

    /// <summary>
    /// Stops the currently running probe task.
    /// </summary>
    [HttpPost("Probe/Stop")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult StopProbe()
    {
        _probeService.StopProbe();
        return NoContent();
    }

    /// <summary>
    /// Returns the current probe task status and progress.
    /// </summary>
    [HttpGet("Probe/Status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetProbeStatus()
    {
        return Ok(new
        {
            _probeService.State,
            Processed = _probeService.ProcessedItems,
            Total = _probeService.TotalItems,
            CurrentItem = _probeService.CurrentItemName
        });
    }

    /// <summary>
    /// Returns the size and status of the background persistence queue across high and low lanes.
    /// </summary>
    [HttpGet("Queue")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetQueue()
    {
        var running = _probeService.ActiveProbes;
        var waiting = _probeService.PersistQueueWaiting;
        var scanning = _probeService.IsScanningLibrary;
        var isBatchRunning = _probeService.IsBatchRunning;
        return Ok(new
        {
            Waiting = waiting,
            Running = running,
            ActiveCount = running,
            QueueCount = waiting,
            IsRunning = isBatchRunning || scanning || running > 0 || waiting > 0,
            Total = _probeService.PersistQueueTotal,
            HighPriorityWaiting = _probeService.HighPriorityQueueWaiting,
            LowPriorityWaiting = _probeService.LowPriorityQueueWaiting,
            ScanningLibrary = scanning,
            CircuitTripped = _probeService.IsCircuitTripped,
            CircuitBreakerOpen = _probeService.IsCircuitTripped,
            CircuitState = _probeService.CircuitState,
            CooldownSecondsRemaining = (int)Math.Ceiling(_probeService.CircuitCooldownRemaining.TotalSeconds),
            ConsecutiveFailures = _probeService.ConsecutiveFailures,
            CacheSize = _probeService.FailureCacheCount,
            Status = scanning ? "Scanning" : (running + waiting == 0 ? "Idle" : running > 0 ? "Running" : "Waiting")
        });
    }

    /// <summary>
    /// Flushes the negative persistence failure cache, allowing previously failed items to be retried immediately.
    /// </summary>
    [HttpPost("Queue/Reset")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult ResetQueue()
    {
        _probeService.ClearFailureCache();
        return NoContent();
    }

    /// <summary>
    /// Manually resets the circuit breaker to closed state.
    /// </summary>
    [HttpPost("CircuitBreaker/Reset")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult ResetCircuitBreaker()
    {
        _probeService.ResetCircuitBreaker();
        return NoContent();
    }

    /// <summary>
    /// Receives live incoming JSON webhook payloads from Radarr, Sonarr, and Whisparr.
    /// Protected via a dedicated query string token; validates strongly-typed payload schema,
    /// debounces filesystem monitor reports to protect SQLite, and enqueues newly detected items into the High Priority lane.
    /// </summary>
    [HttpPost("ArrWebhook")]
    public async Task<ActionResult> HandleArrWebhook([FromBody] ArrWebhookPayload? payload)
    {
        // 1. Immediately validate parsed payload
        if (payload is null || string.IsNullOrWhiteSpace(payload.EventType))
        {
            return BadRequest(new { status = "bad_request", message = "Invalid or missing webhook payload." });
        }

        // 2. Authorize using dedicated WebhookSecret query token
        var config = Plugin.Instance?.Configuration;
        var expectedSecret = config?.WebhookSecret;
        if (!string.IsNullOrEmpty(expectedSecret))
        {
            var providedToken = Request?.Query != null && Request.Query.TryGetValue("token", out var tokenVal) ? tokenVal.ToString() : null;
            if (string.IsNullOrEmpty(providedToken) || !string.Equals(expectedSecret, providedToken, StringComparison.Ordinal))
            {
                _logger.LogWarning("QuickSTRM Webhook: Unauthorized access attempt - invalid or missing security token");
                return Unauthorized(new { status = "unauthorized", message = "Invalid or missing webhook token." });
            }
        }

        try
        {
            // 3. Switch on EventType to extract file path or handle unsupported/test events
            string? incomingPath = null;
            switch (payload.EventType.Trim())
            {
                case "Test":
                case "test":
                case "Grab":
                case "grab":
                    _logger.LogInformation("QuickSTRM Webhook: Received {EventType} event (acknowledged without enqueuing)", payload.EventType);
                    return Ok(new { status = "success", message = $"{payload.EventType} acknowledged", eventType = payload.EventType });

                case "Download":
                case "download":
                    // Radarr/Whisparr: Movie.FilePath or MovieFile.Path; Sonarr: Episodes (Path or SourcePath), EpisodeFile.Path, or Series.Path
                    incomingPath = payload.Movie?.FilePath
                        ?? payload.MovieFile?.Path
                        ?? payload.Movie?.FolderPath
                        ?? ExtractEpisodePath(payload)
                        ?? payload.EpisodeFile?.Path
                        ?? payload.Series?.Path;
                    break;

                case "Rename":
                case "rename":
                    incomingPath = payload.Movie?.FilePath
                        ?? payload.MovieFile?.Path
                        ?? payload.Movie?.FolderPath
                        ?? ExtractEpisodePath(payload)
                        ?? payload.EpisodeFile?.Path
                        ?? payload.Series?.Path;
                    break;

                case "MovieFileDelete":
                case "MovieDelete":
                case "movieFileDelete":
                case "movieDelete":
                    incomingPath = payload.Movie?.FilePath
                        ?? payload.MovieFile?.Path
                        ?? payload.Movie?.FolderPath;
                    break;

                case "EpisodeFileDelete":
                case "SeriesDelete":
                case "episodeFileDelete":
                case "seriesDelete":
                    incomingPath = ExtractEpisodePath(payload)
                        ?? payload.EpisodeFile?.Path
                        ?? payload.Series?.Path;
                    break;

                default:
                    _logger.LogInformation("QuickSTRM Webhook: Ignored unsupported event type '{EventType}'", payload.EventType);
                    return Ok(new { status = "ignored", reason = "unsupported_event_type", eventType = payload.EventType });
            }

            // 4. Validate extracted file path
            if (string.IsNullOrWhiteSpace(incomingPath))
            {
                _logger.LogWarning("QuickSTRM Webhook: Received {EventType} event but could not extract a valid file path", payload.EventType);
                return BadRequest(new { status = "bad_request", message = $"Could not extract a valid file path for {payload.EventType} event." });
            }

            var localPath = _arrIntegrationService.TranslateArrPathToLocal(incomingPath, out var instance);
            if (localPath is null)
            {
                return Ok(new { status = "ignored", reason = "no_matching_instance_path", incomingPath });
            }

            _logger.LogInformation("QuickSTRM Webhook: Received {EventType} event from {Instance} for {Path} -> {LocalPath}", payload.EventType, instance?.Name, incomingPath, localPath);

            // 5. Debounce ILibraryMonitor updates to protect SQLite from concurrent write locks
            _pendingFileSystemChanges.TryAdd(localPath, 0);
            lock (_debounceLock)
            {
                if (_debounceTimer is null)
                {
                    _debounceTimer = new Timer(OnDebounceTimerElapsed, null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _debounceTimer.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
                }
            }

            // 6. Immediately enqueue matching .strm items into High Priority lane
            if (localPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(localPath))
            {
                var item = _libraryManager.FindByPath(localPath, false);
                if (item is not null)
                {
                    _probeService.EnqueueBackgroundPersist(item, highPriority: true);
                }
                else
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2000).ConfigureAwait(false);
                        var newItem = _libraryManager.FindByPath(localPath, false);
                        if (newItem is not null)
                        {
                            _probeService.EnqueueBackgroundPersist(newItem, highPriority: true);
                        }
                    });
                }
            }

            return Ok(new { status = "success", eventType = payload.EventType, localPath });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Error processing *arr webhook payload");
            return Ok(new { status = "error", message = ex.Message });
        }
    }

    private static string? ExtractEpisodePath(ArrWebhookPayload payload)
    {
        if (payload.Episodes is not null)
        {
            foreach (var ep in payload.Episodes)
            {
                if (!string.IsNullOrWhiteSpace(ep.Path))
                {
                    return ep.Path;
                }

                if (!string.IsNullOrWhiteSpace(ep.SourcePath))
                {
                    return ep.SourcePath;
                }
            }
        }

        return null;
    }

    private static void OnDebounceTimerElapsed(object? state)
    {
        ConcurrentDictionary<string, byte> batched;
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            batched = Interlocked.Exchange(ref _pendingFileSystemChanges, new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        }

        if (batched.IsEmpty)
        {
            return;
        }

        var monitor = _staticLibraryMonitor;
        if (monitor is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            _staticLogger?.LogInformation("QuickSTRM: Flushing debounced file system changes for {Count} items with 50ms pacing", batched.Count);

            foreach (var path in batched.Keys)
            {
                try
                {
                    monitor.ReportFileSystemChanged(path);
                }
                catch (Exception ex)
                {
                    _staticLogger?.LogWarning(ex, "QuickSTRM: Error reporting file system change for {Path}", path);
                }

                await Task.Delay(50).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Tests connectivity to all configured *arr instances and provisions/synchronizes webhooks.
    /// Manual override bypass: triggers an immediate, single-attempt sync bypassing the 5-attempt startup retry loop.
    /// </summary>
    [HttpPost("Arr/TestAndSync")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> TestAndSyncArr()
    {
        var results = await _arrIntegrationService.SyncWebhooksAsync().ConfigureAwait(false);
        var config = Plugin.Instance?.Configuration;
        var instances = config?.ArrInstances ?? new List<ArrInstanceConfig>();

        return Ok(new
        {
            SyncedCount = instances.Count(i => i.ProvisionedWebhookId.HasValue),
            TotalCount = instances.Count,
            Results = instances.Select(i => new
            {
                i.Name,
                Synced = i.ProvisionedWebhookId.HasValue,
                i.ProvisionedWebhookId,
                i.LastSyncError,
                i.LastSyncUtc
            })
        });
    }

    /// <summary>
    /// Synchronizes and tests a single configured *arr instance by zero-based index.
    /// Allows 1-click sync/test directly from the instance card on the dashboard without opening the edit form.
    /// </summary>
    [HttpPost("Arr/SyncInstance/{index}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> SyncArrInstance([FromRoute] int index, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || index < 0 || index >= config.ArrInstances.Count)
        {
            return NotFound(new { Message = $"Arr instance at index {index} not found." });
        }

        var instance = config.ArrInstances[index];
        var webhookUrl = $"{_arrIntegrationService.GetEffectiveJellyfinUrl()}/QuickStrm/ArrWebhook?token={config.WebhookSecret}";

        var result = await _arrIntegrationService.SyncSingleInstanceAsync(instance, webhookUrl, ct).ConfigureAwait(false);

        return Ok(new
        {
            Success = result.Success,
            Message = result.Message,
            Instance = new
            {
                instance.Name,
                Synced = instance.ProvisionedWebhookId.HasValue,
                instance.ProvisionedWebhookId,
                instance.LastSyncError,
                instance.LastSyncUtc
            }
        });
    }

    /// <summary>
    /// Removes all auto-provisioned webhooks from all configured *arr instances for a zero-footprint uninstall.
    /// </summary>
    [HttpPost("Arr/WipeWebhooks")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> WipeArrWebhooks()
    {
        var results = await _arrIntegrationService.RemoveAllWebhooksAsync().ConfigureAwait(false);
        return Ok(results);
    }

    /// <summary>
    /// Performs a live connectivity check against an *arr instance without saving configuration or provisioning webhooks.
    /// </summary>
    [HttpPost("Arr/TestConnection")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> TestArrConnection([FromBody] ArrInstanceConfig testConfig, CancellationToken ct)
    {
        var (success, message) = await _arrIntegrationService.TestConnectionAsync(testConfig, ct).ConfigureAwait(false);
        return Ok(new
        {
            Success = success,
            Message = message
        });
    }

    /// <summary>
    /// Removes a configured *arr instance by index.
    /// </summary>
    [HttpDelete("Arr/Instance/{index:int}")]
    [HttpPost("Arr/Instance/{index:int}/Delete")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult DeleteArrInstance([FromRoute] int index)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return NotFound(new { Message = "Plugin configuration not found." });
        }

        lock (config)
        {
            if (index < 0 || index >= config.ArrInstances.Count)
            {
                return BadRequest(new { Message = $"Invalid instance index {index}." });
            }

            var removed = config.ArrInstances[index];
            config.ArrInstances.RemoveAt(index);
            Plugin.Instance?.SaveConfiguration();

            _logger.LogInformation("QuickSTRM: Removed *arr instance {Name} at index {Index}", removed.Name, index);
            _ = _telemetryService.LogEventAsync("ArrSync", $"Removed *arr instance {removed.Name} ({removed.ArrType}).");

            return Ok(new
            {
                Removed = true,
                Instances = config.ArrInstances
            });
        }
    }

    /// <summary>
    /// Bulk URL/path rewriting tool for .strm files across all Jellyfin libraries.
    /// </summary>
    [HttpPost("Tools/RewriteUrls")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> RewriteUrls([FromBody] RewriteUrlsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OldPrefix) || string.IsNullOrWhiteSpace(request.NewPrefix))
        {
            return BadRequest(new { Message = "OldPrefix and NewPrefix must be non-empty strings." });
        }

        var strmItems = _probeService.GetStrmItems();
        var matches = 0;
        var modified = 0;
        var samples = new List<object>();

        foreach (var item in strmItems)
        {
            if (string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path))
            {
                continue;
            }

            var targetUrl = await ProbeService.ReadStrmUrlAsync(item.Path).ConfigureAwait(false);
            if (targetUrl is not null && targetUrl.StartsWith(request.OldPrefix, StringComparison.OrdinalIgnoreCase))
            {
                matches++;
                var newUrl = request.NewPrefix + targetUrl.Substring(request.OldPrefix.Length);

                if (samples.Count < 5)
                {
                    samples.Add(new
                    {
                        Item = item.Name,
                        Path = item.Path,
                        OldUrl = targetUrl,
                        NewUrl = newUrl
                    });
                }

                if (!request.DryRun)
                {
                    try
                    {
                        await System.IO.File.WriteAllTextAsync(item.Path, newUrl).ConfigureAwait(false);
                        _probeService.ClearFailureCacheForItem(item.Id);
                        modified++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "QuickSTRM: Could not rewrite .strm for {Path}", item.Path);
                    }
                }
            }
        }

        return Ok(new
        {
            TotalScanned = strmItems.Count,
            MatchesFound = matches,
            Modified = modified,
            DryRun = request.DryRun,
            Samples = samples
        });
    }
}

/// <summary>
/// Request payload for the bulk STRM URL rewriter tool.
/// </summary>
public class RewriteUrlsRequest
{
    public string OldPrefix { get; set; } = string.Empty;
    public string NewPrefix { get; set; } = string.Empty;
    public bool DryRun { get; set; } = true;
}

/// <summary>
/// Request payload for starting a manual library pre-probe.
/// </summary>
public class ProbeStartRequest
{
    public string? Scope { get; set; }
    public string? Mode { get; set; }
}
