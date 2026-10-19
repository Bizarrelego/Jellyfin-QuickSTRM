using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuickSTRM;

namespace QuickSTRM.Api;

/// <summary>
/// API endpoints for QuickSTRM probe management. Admin-only.
/// </summary>
[ApiController]
[Route("QuickStrm")]
[Authorize(Policy = "RequiresElevation")]
public class QuickStrmController : ControllerBase
{
    private readonly ProbeService _probeService;

    public QuickStrmController(ProbeService probeService)
    {
        _probeService = probeService;
    }

    /// <summary>
    /// Returns a summary of .strm items in the library.
    /// </summary>
    [HttpGet("Summary")]
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
    public ActionResult StopProbe()
    {
        _probeService.StopProbe();
        return NoContent();
    }

    /// <summary>
    /// Returns the current probe task status and progress.
    /// </summary>
    [HttpGet("Probe/Status")]
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
    /// Returns the size and status of the background persistence queue (items probed inline that are being saved to the database).
    /// </summary>
    [HttpGet("Queue")]
    public ActionResult GetQueue()
    {
        var running = _probeService.PersistQueueRunning;
        var waiting = _probeService.PersistQueueWaiting;
        return Ok(new
        {
            // items still waiting for one of the 2 probe slots
            Waiting = waiting,
            // items currently being persisted (holding a probe slot)
            Running = running,
            // waiting + running
            Total = _probeService.PersistQueueTotal,
            CircuitTripped = _probeService.IsCircuitTripped,
            CircuitState = _probeService.CircuitState,
            CooldownSecondsRemaining = (int)System.Math.Ceiling(_probeService.CircuitCooldownRemaining.TotalSeconds),
            ConsecutiveFailures = _probeService.ConsecutiveFailures,
            CacheSize = _probeService.FailureCacheCount,
            Status = running + waiting == 0 ? "Idle" : running > 0 ? "Running" : "Waiting"
        });
    }

    /// <summary>
    /// Flushes the negative persistence failure cache, allowing previously failed items to be retried immediately.
    /// </summary>
    [HttpPost("Queue/Reset")]
    public ActionResult ResetQueue()
    {
        _probeService.ClearFailureCache();
        return NoContent();
    }
}
