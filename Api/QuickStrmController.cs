using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuickSTRM;

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

    public QuickStrmController(
        ProbeService probeService,
        ArrIntegrationService arrIntegrationService,
        ILibraryManager libraryManager,
        ILogger<QuickStrmController> logger,
        ILibraryMonitor? libraryMonitor = null)
    {
        _probeService = probeService;
        _arrIntegrationService = arrIntegrationService;
        _libraryManager = libraryManager;
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
    /// Starts a probe task. Mode: "Incremental" or "Full".
    /// </summary>
    [HttpPost("Probe/Start")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult StartProbe([FromQuery] string mode = "Incremental")
    {
        if (_probeService.StartProbe(mode))
        {
            return NoContent();
        }

        return Conflict(new { Message = "A probe task is already running." });
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
        var running = _probeService.PersistQueueRunning;
        var waiting = _probeService.PersistQueueWaiting;
        return Ok(new
        {
            Waiting = waiting,
            Running = running,
            Total = _probeService.PersistQueueTotal,
            HighPriorityWaiting = _probeService.HighPriorityQueueWaiting,
            LowPriorityWaiting = _probeService.LowPriorityQueueWaiting,
            CircuitTripped = _probeService.IsCircuitTripped,
            CircuitState = _probeService.CircuitState,
            CooldownSecondsRemaining = (int)Math.Ceiling(_probeService.CircuitCooldownRemaining.TotalSeconds),
            ConsecutiveFailures = _probeService.ConsecutiveFailures,
            CacheSize = _probeService.FailureCacheCount,
            Status = running + waiting == 0 ? "Idle" : running > 0 ? "Running" : "Waiting"
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
    /// Receives live incoming JSON webhook payloads from Radarr, Sonarr, and Whisparr.
    /// Protected via a dedicated query string token; debounces filesystem monitor reports to protect SQLite,
    /// and enqueues newly detected items into the High Priority lane immediately.
    /// </summary>
    [HttpPost("ArrWebhook")]
    public async Task<ActionResult> HandleArrWebhook()
    {
        // 1. Authorize using dedicated WebhookSecret query token
        var config = Plugin.Instance?.Configuration;
        var expectedSecret = config?.WebhookSecret;
        var providedToken = Request.Query["token"].ToString();

        if (string.IsNullOrEmpty(expectedSecret) || string.IsNullOrEmpty(providedToken) || !string.Equals(expectedSecret, providedToken, StringComparison.Ordinal))
        {
            _logger.LogWarning("QuickSTRM Webhook: Unauthorized access attempt - invalid or missing security token");
            return Unauthorized(new { status = "unauthorized", message = "Invalid or missing webhook token." });
        }

        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return Ok(new { status = "empty_body" });
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var eventType = root.TryGetProperty("eventType", out var et) ? et.GetString() : "Unknown";

            string? incomingPath = null;
            if (root.TryGetProperty("movieFile", out var mf) && mf.TryGetProperty("path", out var mfp))
            {
                incomingPath = mfp.GetString();
            }
            else if (root.TryGetProperty("episodeFile", out var ef) && ef.TryGetProperty("path", out var efp))
            {
                incomingPath = efp.GetString();
            }
            else if (root.TryGetProperty("movie", out var m) && m.TryGetProperty("folderPath", out var mfp2))
            {
                incomingPath = mfp2.GetString();
            }
            else if (root.TryGetProperty("series", out var s) && s.TryGetProperty("path", out var sfp))
            {
                incomingPath = sfp.GetString();
            }

            if (string.IsNullOrWhiteSpace(incomingPath))
            {
                return Ok(new { status = "ignored", reason = "no_path_found", eventType });
            }

            var localPath = _arrIntegrationService.TranslateArrPathToLocal(incomingPath, out var instance);
            if (localPath is null)
            {
                return Ok(new { status = "ignored", reason = "no_matching_instance_path", incomingPath });
            }

            _logger.LogInformation("QuickSTRM Webhook: Received {EventType} event from {Instance} for {Path} -> {LocalPath}", eventType, instance?.Name, incomingPath, localPath);

            // 2. Debounce ILibraryMonitor updates to protect SQLite from concurrent write locks
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

            // 3. Immediately enqueue matching .strm items into High Priority lane
            if (localPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(localPath))
            {
                var item = _libraryManager.FindByPath(localPath);
                if (item is not null)
                {
                    _probeService.EnqueueBackgroundPersist(item, highPriority: true);
                }
                else
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2000).ConfigureAwait(false);
                        var newItem = _libraryManager.FindByPath(localPath);
                        if (newItem is not null)
                        {
                            _probeService.EnqueueBackgroundPersist(newItem, highPriority: true);
                        }
                    });
                }
            }

            return Ok(new { status = "success", eventType, localPath });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Error processing *arr webhook payload");
            return Ok(new { status = "error", message = ex.Message });
        }
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
        return Ok(results);
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
