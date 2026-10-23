using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuickSTRM;
using QuickSTRM.Configuration;

namespace QuickSTRM.Api;

/// <summary>
/// API endpoints for QuickSTRM probe management, native Auto-Heal feeds, and maintenance tools.
/// </summary>
[ApiController]
[Route("QuickStrm")]
public class QuickStrmController : ControllerBase
{
    private readonly ProbeService _probeService;
    private readonly AutoHealService _autoHealService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor? _libraryMonitor;
    private readonly ILogger<QuickStrmController> _logger;
    private readonly TelemetryService _telemetryService;

    public QuickStrmController(
        ProbeService probeService,
        AutoHealService autoHealService,
        ILibraryManager libraryManager,
        TelemetryService telemetryService,
        ILogger<QuickStrmController> logger,
        ILibraryMonitor? libraryMonitor = null)
    {
        _probeService = probeService;
        _autoHealService = autoHealService;
        _libraryManager = libraryManager;
        _telemetryService = telemetryService;
        _logger = logger;
        _libraryMonitor = libraryMonitor;
    }

    /// <summary>
    /// Returns unified dashboard telemetry, queue metrics, and active auto-heal entries.
    /// </summary>
    [HttpGet("Dashboard")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetDashboard()
    {
        var (totalStrm, probedStrm, unprobedStrm) = _probeService.GetSummary();
        var isScanning = _probeService.IsScanningLibrary;
        var isBatchRunning = _probeService.IsBatchRunning;
        var isRunning = isScanning || isBatchRunning || _probeService.State == "Running";

        var config = Plugin.Instance?.Configuration;
        string[] ledger;
        if (config is not null)
        {
            lock (config)
            {
                ledger = config.ActionLedger.ToArray();
            }
        }
        else
        {
            ledger = Array.Empty<string>();
        }

        return Ok(new
        {
            Queue = new
            {
                IsRunning = isRunning,
                ScanningLibrary = isScanning,
                QueueCount = _probeService.HighPriorityQueueWaiting + _probeService.LowPriorityQueueWaiting,
                ActiveCount = _probeService.ActiveCount,
                ActiveProbes = _probeService.ActiveProbeNames.ToArray(),
                TotalQueued = _probeService.TotalItems,
                CompletedCount = _probeService.ProcessedItems,
                CircuitState = _probeService.CircuitState,
                IsCircuitTripped = _probeService.IsCircuitTripped,
                ConsecutiveFailures = _probeService.ConsecutiveFailures
            },
            Telemetry = new
            {
                ProbedCount = Math.Max(0, totalStrm - unprobedStrm),
                PrewarmedCount = _telemetryService.PrewarmedCount,
                HealedCount = _telemetryService.HealedCount,
                DeletedCount = _telemetryService.DeletedCount,
                TotalStrmCount = totalStrm,
                UnprobedStrmCount = unprobedStrm,
                Ledger = ledger
            },
            AutoHeal = new
            {
                RadarrCount = _autoHealService.RadarrCount,
                SonarrCount = _autoHealService.SonarrCount,
                TotalCount = _autoHealService.TotalCount,
                Items = _autoHealService.GetAllEntries()
            }
        });
    }

    /// <summary>
    /// Returns telemetry metrics and recent ledger events.
    /// </summary>
    [HttpGet("Telemetry")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetTelemetry()
    {
        var (totalStrm, probedStrm, unprobedStrm) = _probeService.GetSummary();
        var config = Plugin.Instance?.Configuration;
        string[] ledger;
        if (config is not null)
        {
            lock (config)
            {
                ledger = config.ActionLedger.ToArray();
            }
        }
        else
        {
            ledger = Array.Empty<string>();
        }

        return Ok(new
        {
            ProbedCount = Math.Max(0, totalStrm - unprobedStrm),
            PrewarmedCount = _telemetryService.PrewarmedCount,
            HealedCount = _telemetryService.HealedCount,
            DeletedCount = _telemetryService.DeletedCount,
            TotalStrmCount = totalStrm,
            UnprobedStrmCount = unprobedStrm,
            Ledger = ledger
        });
    }

    /// <summary>
    /// Returns a summary of .strm items in the library and auto-heal feed metrics.
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
            CacheSize = _probeService.FailureCacheCount,
            AutoHealRadarrCount = _autoHealService.RadarrCount,
            AutoHealSonarrCount = _autoHealService.SonarrCount,
            AutoHealTotalCount = _autoHealService.TotalCount
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
    /// Empties recent action ledger history.
    /// </summary>
    [HttpPost("Ledger/Clear")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult ClearLedger()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is not null)
        {
            lock (config)
            {
                config.ClearLedger();
                Plugin.Instance?.SaveConfiguration();
            }
        }

        return NoContent();
    }

    /// <summary>
    /// Returns the live Radarr recovery feed formatted as a native Custom List JSON array.
    /// </summary>
    [HttpGet("AutoHeal/Radarr")]
    [AllowAnonymous]
    [Produces("application/json")]
    public ActionResult GetRadarrAutoHealFeed()
    {
        return Ok(_autoHealService.GetRadarrFeed());
    }

    /// <summary>
    /// Returns the live Sonarr recovery feed formatted as a native Custom List JSON array.
    /// </summary>
    [HttpGet("AutoHeal/Sonarr")]
    [AllowAnonymous]
    [Produces("application/json")]
    public ActionResult GetSonarrAutoHealFeed()
    {
        return Ok(_autoHealService.GetSonarrFeed());
    }

    /// <summary>
    /// Returns summary metrics for the Auto-Heal list feeds.
    /// </summary>
    [HttpGet("AutoHeal/Summary")]
    [Produces("application/json")]
    public ActionResult GetAutoHealSummary()
    {
        return Ok(new
        {
            RadarrCount = _autoHealService.RadarrCount,
            SonarrCount = _autoHealService.SonarrCount,
            TotalCount = _autoHealService.TotalCount
        });
    }

    /// <summary>
    /// Returns the current probe task status for the live progress bar.
    /// </summary>
    [HttpGet("Status")]
    [HttpGet("Probe/Status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetStatus()
    {
        return Ok(new
        {
            State = _probeService.State,
            TotalItems = _probeService.TotalItems,
            Total = _probeService.TotalItems,
            ProcessedItems = _probeService.ProcessedItems,
            Processed = _probeService.ProcessedItems,
            CurrentItemName = _probeService.CurrentItemName,
            CurrentItem = _probeService.CurrentItemName,
            ActiveProbes = _probeService.ActiveProbes,
            ActiveProbeNames = _probeService.ActiveProbeNames.ToArray(),
            MaxConcurrentProbes = ProbeService.MaxConcurrentProbes
        });
    }

    /// <summary>
    /// Starts a manual background probe scan across the library.
    /// Returns 202 Accepted immediately without blocking the HTTP request thread.
    /// </summary>
    [HttpPost("Probe")]
    [HttpPost("Probe/Start")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> StartProbe([FromQuery] string? mode = null, [FromBody] ProbeRequestDto? body = null)
    {
        var rawMode = mode ?? body?.Mode;
        var probeMode = string.Equals(rawMode, "FullRefresh", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(rawMode, "Full", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(rawMode, "All", StringComparison.OrdinalIgnoreCase)
            ? ProbeMode.FullRefresh
            : ProbeMode.OnlyUnprobed;

        if (_probeService.IsScanningLibrary)
        {
            return Conflict(new { Message = "A probe batch or whole-library scan is already running." });
        }

        await _probeService.QueueLibraryScanAsync(probeMode).ConfigureAwait(false);

        return Accepted(new
        {
            Message = $"Probe queued in {probeMode} mode. Processing asynchronously.",
            Mode = probeMode.ToString(),
            State = "Running",
            Enqueued = _probeService.TotalItems
        });
    }

    /// <summary>
    /// Cancels any active manual probe run.
    /// </summary>
    [HttpPost("Probe/Stop")]
    [HttpPost("Probe/Cancel")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult StopProbe()
    {
        _probeService.StopProbe();
        return NoContent();
    }

    /// <summary>
    /// Returns queue depth, active worker count, circuit breaker status, and failure counts.
    /// </summary>
    [HttpGet("Queue")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetQueueStatus()
    {
        return Ok(new
        {
            HighPriorityQueueDepth = _probeService.HighPriorityQueueWaiting,
            LowPriorityQueueDepth = _probeService.LowPriorityQueueWaiting,
            PersistQueueDepth = _probeService.PersistQueueWaiting,
            ActiveProbes = _probeService.ActiveProbes,
            FailureCacheSize = _probeService.FailureCacheCount,
            CircuitBreakerTripped = _probeService.IsCircuitTripped,
            CircuitBreakerOpen = _probeService.IsCircuitTripped,
            CircuitState = _probeService.CircuitState,
            CircuitCooldownRemainingSeconds = (int)_probeService.CircuitCooldownRemaining.TotalSeconds,
            ConsecutiveFailures = _probeService.ConsecutiveFailures,
            ScanningLibrary = _probeService.IsScanningLibrary,
            IsScanningLibrary = _probeService.IsScanningLibrary
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
    [HttpPost("Circuit/Reset")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult ResetCircuitBreaker()
    {
        _probeService.ResetCircuitBreaker();
        return NoContent();
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
/// Request payload for probe requests.
/// </summary>
public class ProbeRequestDto
{
    public string? Mode { get; set; }
}

/// <summary>
/// Request payload for starting a manual library pre-probe.
/// </summary>
public class ProbeStartRequest : ProbeRequestDto
{
    public string? Scope { get; set; }
}
