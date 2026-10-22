using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using System.Text;
using System.Threading.Channels;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Activity;
using System.Reflection;

namespace QuickSTRM;

/// <summary>
/// Operational mode for manual library pre-probing.
/// </summary>
public enum ProbeMode
{
    Incremental,
    All,
    OnlyUnprobed = Incremental
}

/// <summary>
/// Background service that provides library-wide .strm pre-probing
/// and automatic probing of newly added items.
/// </summary>
public sealed class ProbeService : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IDirectoryService _directoryService;
    private readonly ILogger<ProbeService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _probeTask;
    private readonly object _lock = new();

    private int _isScanningLibrary;

    /// <summary>
    /// Gets a value indicating whether a background library scan is currently running.
    /// </summary>
    public bool IsScanningLibrary => Volatile.Read(ref _isScanningLibrary) == 1;

    // protects memory: one entry per queued/running item ID (not per request), so N repeated play attempts of the same item
    // occupy exactly one slot instead of N parked tasks, each of which would otherwise pin a BaseItem and a closure in RAM
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _pendingPersist = new();

    // number of queued items that already hold a probe slot (running); waiting = pending - running
    private int _activeProbes;
    private int _isBatchRunning;

    /// <summary>Gets the number of items currently being actively probed.</summary>
    public int ActiveProbes => Volatile.Read(ref _activeProbes);

    /// <summary>Gets the number of items currently being actively probed.</summary>
    public int ActiveCount => ActiveProbes;

    /// <summary>Gets a value indicating whether a batch probe or scan operation is actively running.</summary>
    public bool IsBatchRunning => Volatile.Read(ref _isBatchRunning) == 1;

    // protects memory on a low-end host: hard bound on queued items, so even a flood of DISTINCT items can't grow without limit
    private const int MaxPendingPersist = 500;

    // negative cache of items whose background probe failed or timed out; dead URLs are rejected here until the entry expires
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> _failedPersistCache = new();

    // in-flight probe coalescing to avoid duplicate ffprobe or hold-open loops across concurrent callers
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task<bool>> _inflightProbes = new();

    // absolute expiration: entry expires exactly 1 hour after failure regardless of how often it is queried;
    // prevents perpetual lockouts from automated client retries continuously resetting a sliding window
    private static readonly TimeSpan FailedPersistAbsoluteExpiration = TimeSpan.FromHours(1);

    private readonly Channel<BaseItem> _highPriorityChannel = Channel.CreateBounded<BaseItem>(
        new BoundedChannelOptions(MaxPendingPersist) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<BaseItem> _lowPriorityChannel = Channel.CreateBounded<BaseItem>(
        new BoundedChannelOptions(MaxPendingPersist) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly List<Task> _workerTasks = new();
    private readonly IItemRepository? _itemRepository;

    /// <summary>
    /// Event raised when a dead .strm file is deleted due to permanent 404 failure,
    /// enabling external services (such as ArrIntegrationService) to trigger auto-healing.
    /// </summary>
    public event Func<string, Task>? OnStrmDeleted;

    /// <summary>Gets the number of items waiting in the High Priority lane.</summary>
    public int HighPriorityQueueWaiting => _highPriorityChannel.Reader.Count;

    /// <summary>Gets the number of items waiting in the Low Priority lane.</summary>
    public int LowPriorityQueueWaiting => _lowPriorityChannel.Reader.Count;

    /// <summary>Gets the number of items queued for background persistence that are still waiting for a probe slot.</summary>
    public int PersistQueueWaiting => HighPriorityQueueWaiting + LowPriorityQueueWaiting;

    /// <summary>Gets the number of queued items currently being persisted (holding a probe slot).</summary>
    public int PersistQueueRunning => ActiveProbes;

    /// <summary>Gets the total number of items queued or running for background persistence.</summary>
    public int PersistQueueTotal => _pendingPersist.Count;

    /// <summary>Gets the number of items currently in the negative failure cache.</summary>
    public int FailureCacheCount => _failedPersistCache.Count;

    // protects low-end CPUs (e.g. Core i3): hard ceiling of exactly 2 concurrent ffprobe-backed refreshes across ALL callers
    // (bulk, auto-probe and inline), so probes can never exceed the 2 threads we can spare without starving playback
    private const int MaxConcurrentProbes = 2;
    private readonly SemaphoreSlim _probeGate = new(MaxConcurrentProbes, MaxConcurrentProbes);

    // protects bulk imports: generous 60s lets slow cloud-drive links finish without failing the library scan
    private static readonly TimeSpan BackgroundProbeTimeout = TimeSpan.FromSeconds(60);

    // protects connected clients: inline (playback-time) probes allow hold-open retry window (~8s) plus probe execution
    private static readonly TimeSpan InlineProbeTimeout = TimeSpan.FromSeconds(15);

    // Circuit breaker: consecutive probe/HEAD failures beyond this trip the breaker (a cloud mount drop looks like mass failure)
    private const int CircuitBreakerThreshold = 10;
    private int _consecutiveFailures;
    private volatile bool _circuitTripped;

    // serialises circuit STATE TRANSITIONS (trip / close / reset) so check-then-act races can't double-trip, double-notify or
    // resurrect a manually reset breaker; the hot-path reads (_circuitTripped, ticks, trial CAS) stay lock-free
    private readonly object _circuitLock = new();

    // cancelled on StopAsync/Dispose: every fire-and-forget probe, HEAD request and ffprobe-owning RefreshMetadata call is tied to it,
    // so Jellyfin shutdown/plugin unload kills in-flight work (and the ffprobe child processes) immediately
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed;

    // auto-recovery: after tripping, the breaker stays open for this long, then goes half-open and lets exactly ONE trial through
    private static readonly TimeSpan CircuitCooldown = TimeSpan.FromMinutes(5);
    private long _circuitTrippedAtTicks; // UTC ticks; 0 = not tripped (long so reads/writes are atomic)
    private int _halfOpenInFlight;       // 0/1: guards the single half-open trial request
    private readonly IActivityManager _activityManager;

    // pre-flight HEAD must answer within 500ms or the link is treated as dead, so no ffprobe slot is spent on it
    private static readonly TimeSpan PreflightTimeout = TimeSpan.FromMilliseconds(500);
    // instance (not static) so it is disposed on plugin unload and doesn't pin the plugin's AssemblyLoadContext; a bounded connection
    // lifetime still gives DNS refresh without socket churn
    private readonly HttpClient _preflightClient = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    // caps simultaneous pre-flight HEADs (e.g. a 50-item season pack import) to avoid socket exhaustion, remote 429s and queue-induced
    // false timeouts; entirely separate from _probeGate, which still governs ffprobe processes
    private const int MaxConcurrentHeads = 10;
    private readonly SemaphoreSlim _headGate = new(MaxConcurrentHeads, MaxConcurrentHeads);

    /// <summary>Gets a value indicating whether the circuit breaker is tripped (queue processing and auto-delete halted).</summary>
    public bool IsCircuitTripped => _circuitTripped;

    /// <summary>Gets when the breaker last tripped (UTC), or null while closed.</summary>
    public DateTime? CircuitTrippedAt
        => !_circuitTripped || Interlocked.Read(ref _circuitTrippedAtTicks) == 0
            ? null
            : new DateTime(Interlocked.Read(ref _circuitTrippedAtTicks), DateTimeKind.Utc);

    /// <summary>Gets the time left before a half-open trial is allowed (zero when closed or already elapsed).</summary>
    public TimeSpan CircuitCooldownRemaining
    {
        get
        {
            var at = CircuitTrippedAt;
            if (at is null)
            {
                return TimeSpan.Zero;
            }

            var left = CircuitCooldown - (DateTime.UtcNow - at.Value);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Gets Closed, Open (cooling down) or HalfOpen (cooldown elapsed, trial allowed/in flight).</summary>
    public string CircuitState => !_circuitTripped ? "Closed" : CircuitCooldownRemaining > TimeSpan.Zero ? "Open" : "HalfOpen";

    /// <summary>Gets the current number of consecutive probe/HEAD failures.</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>Gets the current task state: Idle, Running, or Stopping.</summary>
    public string State { get; private set; } = "Idle";

    /// <summary>Gets the total number of items in the current probe batch.</summary>
    public int TotalItems { get; private set; }

    /// <summary>Gets the number of items already processed.</summary>
    public int ProcessedItems { get; private set; }

    /// <summary>Gets the name of the item currently being probed.</summary>
    public string CurrentItemName { get; private set; } = string.Empty;

    private readonly TelemetryService _telemetryService;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMediaSourceManager? _mediaSourceManager;

    public ProbeService(
        ILibraryManager libraryManager,
        IDirectoryService directoryService,
        IMediaEncoder mediaEncoder,
        IMemoryCache? memoryCache = null,
        IActivityManager activityManager = null!,
        ILogger<ProbeService> logger = null!,
        TelemetryService telemetryService = null!,
        IItemRepository? itemRepository = null,
        IMediaSourceManager? mediaSourceManager = null)
    {
        _telemetryService = telemetryService;
        _activityManager = activityManager;
        _libraryManager = libraryManager;
        _directoryService = directoryService;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _itemRepository = itemRepository;
        _mediaSourceManager = mediaSourceManager;
    }

    // ── IHostedService lifecycle ──

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;

        // Launch worker loops for the two-lane priority queue
        for (var i = 0; i < MaxConcurrentProbes; i++)
        {
            _workerTasks.Add(Task.Run(() => ProcessQueueLoopAsync(_shutdownCts.Token)));
        }

        // Launch background maintenance loop (periodic cache sweep every 10 minutes)
        _workerTasks.Add(Task.Run(() => MaintenanceLoopAsync(_shutdownCts.Token)));

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _shutdownCts.Cancel();
        _highPriorityChannel.Writer.TryComplete();
        _lowPriorityChannel.Writer.TryComplete();
        StopProbe();

        try
        {
            await Task.WhenAll(_workerTasks).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    // ── Auto-probe on item added ──

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is not { EnableAutoProbe: true })
        {
            return;
        }

        var item = e.Item;
        if (!IsStrmItem(item) || HasStreamInfo(item))
        {
            return;
        }

        // protects low-end CPUs and memory: routed through the SAME bounded (500), de-duplicated, negative-cached queue as playback
        // persistence, so a bulk import can no longer spawn one unbounded parked Task per added item; the 2-slot gate still applies
        _logger.LogInformation("QuickSTRM: Auto-probing new item {Name}", item.Name);
        EnqueueBackgroundPersist(item);
    }

    // ── Public API ──

    /// <summary>
    /// Returns a summary of .strm items in the library.
    /// </summary>
    public (int Total, int Probed, int Unprobed) GetSummary()
    {
        var items = GetStrmItems();
        int probed = items.Count(HasStreamInfo);
        return (items.Count, probed, items.Count - probed);
    }

    /// <summary>
    /// Playback-time fast path: probes every given source through <see cref="IMediaEncoder"/> entirely in memory under ONE
    /// shared budget. Does NOT touch the database or run a metadata refresh.
    /// The result is aligned with <paramref name="sources"/>; an entry is null if that source timed out, got no slot or failed.
    /// </summary>
    public async Task<MediaInfo?[]> ProbeSourcesInlineAsync(
        IReadOnlyList<MediaSourceInfo> sources,
        CancellationToken ct,
        BaseItem? item = null)
    {
        // protects low-end CPUs: ONE linked token carries the total cap and the client's cancellation into every encoder call,
        // so N sources can never stretch playback start to N x timeout, and cancellation reaches the process owner (the encoder)
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(InlineProbeTimeout);

        // improves latency: sources are probed concurrently, while the shared 2-slot gate keeps at most 2 ffprobe processes alive
        var tasks = new Task<MediaInfo?>[sources.Count];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = ProbeOneInlineAsync(sources[i], budget.Token, ct, item);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Fire-and-forget: queues a standard background RefreshMetadata pass so the stream info is stored in the database
    /// and later plays take the zero-probe fast path. High-priority lane is used for live webhooks, playback fallbacks, and next-episode pre-warming.
    /// </summary>
    public bool EnqueueBackgroundPersist(BaseItem item, bool highPriority = false, bool force = false)
    {
        // protects memory: already-persisted items never enter the queue unless forced
        if (!force && HasStreamInfo(item))
        {
            return false;
        }

        // prevents thread pool starvation on constrained hardware: a known-dead URL is rejected before any Task is created
        if (_failedPersistCache.TryGetValue(item.Id, out var expiresAt))
        {
            if (expiresAt > DateTime.UtcNow)
            {
                return false;
            }
            _failedPersistCache.TryRemove(item.Id, out _);
        }

        // an open breaker halts queue processing; once cooldown has elapsed requests may queue
        if (_circuitTripped && (CircuitCooldownRemaining > TimeSpan.Zero || Volatile.Read(ref _halfOpenInFlight) != 0))
        {
            return false;
        }

        // protects memory: bounded queue ceiling across both lanes
        if (_pendingPersist.Count >= MaxPendingPersist)
        {
            _logger.LogWarning("QuickSTRM: Persist queue full ({Max}); dropping {Name}", MaxPendingPersist, item.Name);
            return false;
        }

        // atomic deduplication: concurrent plays/requests of the same item race safely and exactly one wins
        if (!_pendingPersist.TryAdd(item.Id, 0))
        {
            return false;
        }

        var channel = highPriority ? _highPriorityChannel : _lowPriorityChannel;
        if (!channel.Writer.TryWrite(item))
        {
            _pendingPersist.TryRemove(item.Id, out _);
            _logger.LogWarning("QuickSTRM: Could not enqueue {Priority} priority item {Name}; channel full", highPriority ? "High" : "Low", item.Name);
            return false;
        }

        Interlocked.Exchange(ref _isBatchRunning, 1);
        return true;
    }

    /// <summary>
    /// Enqueues multiple items for background persistence and marks batch running.
    /// </summary>
    public void EnqueueItems(IEnumerable<BaseItem> items, bool highPriority = false, bool force = false)
    {
        Interlocked.Exchange(ref _isBatchRunning, 1);
        foreach (var item in items)
        {
            EnqueueBackgroundPersist(item, highPriority, force);
        }
    }

    private async Task ProcessQueueLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            BaseItem? item = null;

            // 1. Always prioritize High Priority items first
            if (_highPriorityChannel.Reader.TryRead(out item))
            {
            }
            // 2. Fall back to Low Priority lane
            else if (_lowPriorityChannel.Reader.TryRead(out item))
            {
            }
            else
            {
                // 3. Await whichever channel has an item available
                try
                {
                    var highWait = _highPriorityChannel.Reader.WaitToReadAsync(ct).AsTask();
                    var lowWait = _lowPriorityChannel.Reader.WaitToReadAsync(ct).AsTask();
                    var winner = await Task.WhenAny(highWait, lowWait).ConfigureAwait(false);

                    if (await winner.ConfigureAwait(false))
                    {
                        if (!_highPriorityChannel.Reader.TryRead(out item))
                        {
                            _lowPriorityChannel.Reader.TryRead(out item);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }

            if (item is null)
            {
                continue;
            }

            // Immediately increment _activeProbes on consumer loop thread BEFORE probe task execution
            Interlocked.Increment(ref _activeProbes);
            try
            {
                var attempt = await ProbeItemAsync(
                    item,
                    BackgroundProbeTimeout,
                    ct).ConfigureAwait(false);

                if (attempt == ProbeAttempt.SkippedDueToTrial)
                {
                    continue;
                }

                if (attempt != ProbeAttempt.Completed || !HasStreamInfo(item))
                {
                    MarkPersistFailed(item);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "QuickSTRM: Background persist failed for {Name}", item.Name);
                MarkPersistFailed(item);
            }
            finally
            {
                _pendingPersist.TryRemove(item.Id, out _);
                Interlocked.Decrement(ref _activeProbes);
                if (Volatile.Read(ref _activeProbes) == 0 &&
                    _highPriorityChannel.Reader.Count == 0 &&
                    _lowPriorityChannel.Reader.Count == 0 &&
                    Volatile.Read(ref _isScanningLibrary) == 0)
                {
                    Interlocked.Exchange(ref _isBatchRunning, 0);
                }
            }
        }
    }

    /// <summary>
    /// Removes a specific item from the negative failure cache so it can be re-probed immediately.
    /// </summary>
    public void ClearFailureCacheForItem(Guid itemId)
    {
        _failedPersistCache.TryRemove(itemId, out _);
    }

    /// <summary>
    /// Flushes the negative failure cache, allowing previously failed items to be retried immediately.
    /// </summary>
    public void ClearFailureCache()
    {
        _failedPersistCache.Clear();
        _resolvedUrlCache.Clear();
        ResetCircuitBreaker();
        _logger.LogInformation("QuickSTRM: Flushed negative persistence failure cache and resolved URL cache");
    }

    /// <summary>Manual override: closes the breaker immediately and zeroes the failure counter.</summary>
    public void ResetCircuitBreaker()
    {
        lock (_circuitLock)
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            Interlocked.Exchange(ref _circuitTrippedAtTicks, 0);
            Interlocked.Exchange(ref _halfOpenInFlight, 0);
            if (_circuitTripped)
            {
                _circuitTripped = false;
                _logger.LogInformation("QuickSTRM: Circuit breaker manually reset to closed");
                _ = _telemetryService.LogCircuitBreakerAsync("Reset", "Circuit breaker manually reset to closed.");
            }
        }
    }

    /// <summary>Classifies a failure; only <see cref="PermanentFailure"/> may ever lead to deletion.</summary>
    private enum FailureKind
    {
        /// <summary>Timeouts, 429, 502, garbage/empty ffprobe output: counted and negative-cached, never deleted.</summary>
        TemporaryFailure,

        /// <summary>Deterministic Not Found, strictly: an HTTP 404 on the HEAD check, or the .strm file itself missing from the library.</summary>
        PermanentFailure
    }

    private void RecordSuccess() => Interlocked.Exchange(ref _consecutiveFailures, 0);

    /// <summary>Records a failure; returns true if the breaker is (now) tripped.</summary>
    private bool RecordFailure()
    {
        var n = Interlocked.Increment(ref _consecutiveFailures);
        if (n > CircuitBreakerThreshold && !_circuitTripped)
        {
            lock (_circuitLock)
            {
                // re-check under the lock: concurrent failures trip (and notify) exactly once
                if (!_circuitTripped)
                {
                    Interlocked.Exchange(ref _circuitTrippedAtTicks, DateTime.UtcNow.Ticks); // before the flag: readers never see tripped+0
                    _circuitTripped = true;
                    _logger.LogCritical(
                        "QuickSTRM: Circuit breaker TRIPPED after {Count} consecutive failures. Queue processing and auto-deletion halted; retrying automatically in {Minutes} minutes (or reset manually)",
                        n,
                        CircuitCooldown.TotalMinutes);
                    _ = _telemetryService.LogCircuitBreakerAsync(
                        "Tripped",
                        $"Circuit breaker tripped after {n} consecutive failures. Cloud mount down. Retrying in {CircuitCooldown.TotalMinutes:0} minutes.");
                }
            }
        }

        return _circuitTripped;
    }

    /// <summary>
    /// Decides whether a request may proceed. Closed: yes. Open (cooldown running): no. Half-open (cooldown elapsed): exactly one
    /// caller wins the trial slot; everyone else is refused until the trial resolves.
    /// </summary>
    private bool TryEnterCircuit(out bool isTrial)
    {
        isTrial = false;
        if (!_circuitTripped)
        {
            return true;
        }

        if (CircuitCooldownRemaining > TimeSpan.Zero)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _halfOpenInFlight, 1, 0) != 0)
        {
            return false;
        }

        // a thread that read "tripped + cooldown elapsed" may have lost a race with a trial that already CLOSED the circuit (releasing the
        // lock). Re-check after winning the CAS so it never becomes a bogus second trial.
        if (!_circuitTripped)
        {
            Interlocked.Exchange(ref _halfOpenInFlight, 0);
            return true;
        }

        isTrial = true;
        _logger.LogInformation("QuickSTRM: Circuit breaker half-open; allowing one trial request");
        return true;
    }

    // resolves the half-open trial: success closes the circuit; failure re-trips it and restarts the 5-minute cooldown
    private void ResolveTrial(bool success)
    {
        lock (_circuitLock)
        {
            // a manual reset during the trial already closed the circuit: the stale outcome must not re-trip it or re-notify
            if (_circuitTripped)
            {
                if (success)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    Interlocked.Exchange(ref _circuitTrippedAtTicks, 0);
                    _circuitTripped = false;
                    _logger.LogInformation("QuickSTRM: Half-open trial succeeded; circuit breaker closed, queue processing resumed");
                    _ = _telemetryService.LogCircuitBreakerAsync("Reset", "Half-open trial succeeded; circuit breaker closed, queue processing resumed.");
                }
                else
                {
                    Interlocked.Exchange(ref _circuitTrippedAtTicks, DateTime.UtcNow.Ticks);
                    _logger.LogCritical(
                        "QuickSTRM: Half-open trial failed; circuit breaker re-tripped, next retry in {Minutes} minutes",
                        CircuitCooldown.TotalMinutes);
                }
            }

            // released last: until the state above is final, other threads still see the trial as in flight
            Interlocked.Exchange(ref _halfOpenInFlight, 0);
        }
    }

    // fire-and-forget admin notification in Jellyfin's activity log; never allowed to throw into the probe path
    private void Notify(string message)
    {
        _ = _telemetryService.LogEventAsync("QuickSTRM", message);
    }

    /// <summary>
    /// Pre-flight connectivity check: a 500ms HEAD on the .strm URL before any ffprobe slot is used. Returns false when the host
    /// answers 404/502/429, errors, or times out; in that case the item is negative-cached and counted by the circuit breaker.
    /// Only a 404 or a verifiably deleted .strm file may lead to deletion (if enabled and the breaker is closed).
    /// HEAD requests never touch the 2-slot ffprobe gate, and are themselves capped by the 10-slot _headGate.
    /// While the breaker is open this refuses immediately. It never takes the half-open trial and never changes the circuit
    /// state: the trial is owned by ProbeItemAsync, which holds it until ffprobe has really succeeded or failed.
    /// </summary>
    public async Task<bool> PreflightAsync(BaseItem item, CancellationToken ct)
    {
        if (_circuitTripped)
        {
            return false;
        }

        return await PreflightCoreAsync(item, ct).ConfigureAwait(false);
    }

    // Hold-open retry loop delays: 4 attempts over ~8s total (1s, 2s, 2s, 3s)
    private static readonly TimeSpan[] HoldOpenRetryDelays =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3)
    };

    private const string FfmpegUserAgent = "Lavf/60.3.100";

    private sealed record ResolvedUrlEntry(string ResolvedUrl, DateTime ExpiresAt);

    // Cache of pre-resolved direct URLs to bypass redirect hops on clients like Fire TV (15-minute TTL)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ResolvedUrlEntry> _resolvedUrlCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ResolvedUrlCacheTtl = TimeSpan.FromMinutes(15);

    private static bool TryGetResolvedUrl(string url, out string? resolvedUrl)
    {
        if (_resolvedUrlCache.TryGetValue(url, out var entry))
        {
            if (DateTime.UtcNow < entry.ExpiresAt)
            {
                resolvedUrl = entry.ResolvedUrl;
                return true;
            }

            _resolvedUrlCache.TryRemove(url, out _);
        }

        resolvedUrl = null;
        return false;
    }

    private static void CacheResolvedUrl(string url, string resolvedUrl)
    {
        if (_resolvedUrlCache.Count > 1000)
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _resolvedUrlCache)
            {
                if (now >= kvp.Value.ExpiresAt)
                {
                    _resolvedUrlCache.TryRemove(kvp.Key, out _);
                }
            }
        }

        _resolvedUrlCache[url] = new ResolvedUrlEntry(resolvedUrl, DateTime.UtcNow.Add(ResolvedUrlCacheTtl));
    }

    // Lightweight client with AllowAutoRedirect disabled for single-hop redirect pre-resolution
    private static readonly HttpClient _redirectResolutionClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    /// <summary>
    /// Reads only the first non-empty line of a .strm file up to 4096 characters,
    /// avoiding large object heap (LOH) allocations on corrupted or binary files.
    /// Executes a lightweight HTTP HEAD request with User-Agent: Lavf/60.3.100 and AllowAutoRedirect = false
    /// to pre-resolve 301, 302, 307, and 308 redirects, falling back to GET Range: bytes=0-0 on 405/403,
    /// with a 15-minute cache TTL and a hold-open retry loop for newly added / mounting targets.
    /// </summary>
    public static async Task<string?> ReadStrmUrlAsync(string path, CancellationToken ct = default)
    {
        FileStream? stream = null;
        try
        {
            for (var attempt = 0; attempt <= HoldOpenRetryDelays.Length; attempt++)
            {
                try
                {
                    stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        bufferSize: 4096,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                    break;
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException && attempt < HoldOpenRetryDelays.Length)
                {
                    await Task.Delay(HoldOpenRetryDelays[attempt], ct).ConfigureAwait(false);
                }
            }

            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[4096];
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            var text = new string(buffer, 0, read);
            using var lineReader = new StringReader(text);
            string? line;
            while ((line = lineReader.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                // If target local path does not exist yet: hold-open retry loop
                if (Path.IsPathRooted(trimmed) && !trimmed.Contains("://", StringComparison.Ordinal))
                {
                    if (File.Exists(trimmed))
                    {
                        return trimmed;
                    }

                    foreach (var delay in HoldOpenRetryDelays)
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        if (File.Exists(trimmed))
                        {
                            return trimmed;
                        }
                    }

                    return trimmed;
                }

                if (TryGetResolvedUrl(trimmed, out var cached))
                {
                    return cached;
                }

                if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    return await ResolveHttpRedirectWithHoldOpenAsync(trimmed, uri, ct).ConfigureAwait(false);
                }

                return trimmed;
            }

            return null;
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private const int MaxRedirectHops = 5;

    private static async Task<string> ResolveHttpRedirectWithHoldOpenAsync(string originalUrl, Uri uri, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= HoldOpenRetryDelays.Length; attempt++)
        {
            try
            {
                var currentUri = uri;
                var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentUri.AbsoluteUri };
                var reachedTerminal = false;

                for (var hop = 0; hop < MaxRedirectHops; hop++)
                {
                    using var redirectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    redirectCts.CancelAfter(TimeSpan.FromSeconds(3));

                    using var request = new HttpRequestMessage(HttpMethod.Head, currentUri);
                    request.Headers.TryAddWithoutValidation("User-Agent", FfmpegUserAgent);

                    using var response = await _redirectResolutionClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        redirectCts.Token).ConfigureAwait(false);

                    var statusCode = response.StatusCode;
                    Uri? nextLocation = null;

                    if (statusCode is HttpStatusCode.Moved
                        or HttpStatusCode.Redirect
                        or HttpStatusCode.TemporaryRedirect
                        or (HttpStatusCode)308)
                    {
                        if (response.Headers.Location is not null)
                        {
                            nextLocation = response.Headers.Location.IsAbsoluteUri
                                ? response.Headers.Location
                                : new Uri(currentUri, response.Headers.Location);
                        }
                    }
                    else if (statusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden)
                    {
                        using var getCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        getCts.CancelAfter(TimeSpan.FromSeconds(3));

                        using var getRequest = new HttpRequestMessage(HttpMethod.Get, currentUri);
                        getRequest.Headers.TryAddWithoutValidation("User-Agent", FfmpegUserAgent);
                        getRequest.Headers.TryAddWithoutValidation("Range", "bytes=0-0");

                        using var getResponse = await _redirectResolutionClient.SendAsync(
                            getRequest,
                            HttpCompletionOption.ResponseHeadersRead,
                            getCts.Token).ConfigureAwait(false);

                        statusCode = getResponse.StatusCode;

                        if (statusCode is HttpStatusCode.Moved
                            or HttpStatusCode.Redirect
                            or HttpStatusCode.TemporaryRedirect
                            or (HttpStatusCode)308)
                        {
                            if (getResponse.Headers.Location is not null)
                            {
                                nextLocation = getResponse.Headers.Location.IsAbsoluteUri
                                    ? getResponse.Headers.Location
                                    : new Uri(currentUri, getResponse.Headers.Location);
                            }
                        }
                    }

                    if (nextLocation is not null)
                    {
                        if (!visitedUrls.Add(nextLocation.AbsoluteUri))
                        {
                            // Guard against redirect loops
                            reachedTerminal = true;
                            break;
                        }

                        currentUri = nextLocation;
                        continue;
                    }

                    // If 404, 502, 503, 504 -> InfiniDysk mount race condition, retry
                    if (statusCode is HttpStatusCode.NotFound
                        or HttpStatusCode.BadGateway
                        or HttpStatusCode.ServiceUnavailable
                        or HttpStatusCode.GatewayTimeout)
                    {
                        break;
                    }

                    reachedTerminal = true;
                    break;
                }

                if (reachedTerminal)
                {
                    var finalUrl = currentUri.ToString();
                    CacheResolvedUrl(originalUrl, finalUrl);
                    return finalUrl;
                }

                if (attempt < HoldOpenRetryDelays.Length)
                {
                    await Task.Delay(HoldOpenRetryDelays[attempt], ct).ConfigureAwait(false);
                    continue;
                }

                return originalUrl;
            }
            catch (Exception) when (attempt < HoldOpenRetryDelays.Length)
            {
                await Task.Delay(HoldOpenRetryDelays[attempt], ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return originalUrl;
            }
        }

        return originalUrl;
    }

    private async Task<bool> PreflightCoreAsync(BaseItem item, CancellationToken ct)
    {
        string? url;
        try
        {
            url = await ReadStrmUrlAsync(item.Path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failureKind = FailureKind.TemporaryFailure;
            if (ex is FileNotFoundException && IsParentDirectoryReadable(item.Path))
            {
                failureKind = FailureKind.PermanentFailure;
            }
            else if (ex is not (FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException))
            {
                _logger.LogWarning(ex, "QuickSTRM: Cannot read .strm file {Path}", item.Path);
                return true;
            }

            _logger.LogWarning(
                ex,
                "QuickSTRM: .strm file unavailable for {Name}: {Path} ({Kind})",
                item.Name,
                item.Path,
                failureKind);
            MarkPersistFailed(item);
            HandleFailure(item, failureKind);
            return false;
        }

        if (url is not null && Path.IsPathRooted(url) && !url.Contains("://", StringComparison.Ordinal))
        {
            if (File.Exists(url))
            {
                RecordSuccess();
                return true;
            }

            _logger.LogInformation("QuickSTRM: Local target missing for {Name} ({Url}); entering hold-open retry loop (up to ~8s)", item.Name, url);
            var localExists = false;
            foreach (var delay in HoldOpenRetryDelays)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                if (File.Exists(url))
                {
                    localExists = true;
                    break;
                }
            }

            if (localExists)
            {
                _logger.LogInformation("QuickSTRM: Local target appeared after hold-open retry for {Name}: {Url}", item.Name, url);
                RecordSuccess();
                return true;
            }

            _logger.LogWarning("QuickSTRM: Local .strm target missing after hold-open retry window for {Name}: {Url} (treated as temporary; possible dropped mount)", item.Name, url);
            MarkPersistFailed(item);
            HandleFailure(item, FailureKind.TemporaryFailure);
            return false;
        }

        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return true;
        }

        await _headGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await CheckHttpEndpointWithHoldOpenRetryAsync(uri, ct).ConfigureAwait(false);
            if (result.IsHealthy)
            {
                RecordSuccess();
                return true;
            }

            _logger.LogWarning(
                "QuickSTRM: Pre-flight check failed after hold-open retries for {Name}; status {Status} ({Kind})",
                item.Name,
                result.StatusCode,
                result.FailureKind);

            MarkPersistFailed(item);
            HandleFailure(item, result.FailureKind);
            return false;
        }
        finally
        {
            ReleaseQuietly(_headGate);
        }
    }

    private readonly struct HttpPreflightResult
    {
        public bool IsHealthy { get; }
        public HttpStatusCode? StatusCode { get; }
        public FailureKind FailureKind { get; }

        public HttpPreflightResult(bool isHealthy, HttpStatusCode? statusCode, FailureKind failureKind)
        {
            IsHealthy = isHealthy;
            StatusCode = statusCode;
            FailureKind = failureKind;
        }
    }

    private async Task<HttpPreflightResult> CheckHttpEndpointWithHoldOpenRetryAsync(Uri uri, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= HoldOpenRetryDelays.Length; attempt++)
        {
            HttpStatusCode? statusCode = null;
            var isDead = false;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(PreflightTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", FfmpegUserAgent);

                using var response = await _preflightClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                statusCode = response.StatusCode;

                // Fall back to GET bytes=0-0 on 405 Method Not Allowed or 403 Forbidden
                if (statusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden)
                {
                    using var getCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    getCts.CancelAfter(PreflightTimeout);
                    using var getRequest = new HttpRequestMessage(HttpMethod.Get, uri);
                    getRequest.Headers.TryAddWithoutValidation("User-Agent", FfmpegUserAgent);
                    getRequest.Headers.TryAddWithoutValidation("Range", "bytes=0-0");

                    using var getResponse = await _preflightClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, getCts.Token).ConfigureAwait(false);
                    statusCode = getResponse.StatusCode;
                }

                if (statusCode is HttpStatusCode.NotFound
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout)
                {
                    isDead = true;
                }
                else if (statusCode is HttpStatusCode.TooManyRequests)
                {
                    isDead = true;
                }
                else
                {
                    return new HttpPreflightResult(true, statusCode, FailureKind.TemporaryFailure);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                isDead = true;
            }
            catch (HttpRequestException)
            {
                isDead = true;
            }

            if (!isDead)
            {
                return new HttpPreflightResult(true, statusCode, FailureKind.TemporaryFailure);
            }

            if (attempt < HoldOpenRetryDelays.Length)
            {
                var delay = HoldOpenRetryDelays[attempt];
                _logger.LogInformation(
                    "QuickSTRM: Endpoint {Uri} returned {Status}; hold-open retry {Attempt}/4 waiting {Delay}s",
                    uri,
                    statusCode,
                    attempt + 1,
                    delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            else
            {
                var kind = statusCode == HttpStatusCode.NotFound ? FailureKind.PermanentFailure : FailureKind.TemporaryFailure;
                return new HttpPreflightResult(false, statusCode, kind);
            }
        }

        return new HttpPreflightResult(false, null, FailureKind.TemporaryFailure);
    }

    // maximum number of directories examined (parent first, then ancestors) when searching for the mount marker file
    private const int MaxMarkerSearchDepth = 5;

    // true only if the .strm's parent directory exists, can be read, AND the mount looks alive: with a marker configured the marker
    // must be found within MaxMarkerSearchDepth levels; without one the directory must not be empty. Any I/O error or failed check means
    // we cannot prove the file was deliberately removed, so the caller must treat the failure as temporary
    private static bool IsParentDirectoryReadable(string? path)
    {
        try
        {
            var parent = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return false;
            }

            var marker = Plugin.Instance?.Configuration?.MountMarkerFileName?.Trim();
            if (!string.IsNullOrEmpty(marker))
            {
                // Path A: walk up (bounded) looking for the marker; a dropped mount leaves an empty mount point without it
                var dir = parent;
                for (var depth = 0; depth < MaxMarkerSearchDepth && !string.IsNullOrEmpty(dir); depth++)
                {
                    if (File.Exists(Path.Combine(dir, marker)))
                    {
                        return true;
                    }

                    dir = Path.GetDirectoryName(dir);
                }

                return false;
            }

            // Path B: no marker configured; a completely empty directory is assumed to be a dropped mount
            return Directory.EnumerateFileSystemEntries(parent).Any();
        }
        catch (Exception)
        {
            // any I/O fault (IOException, access violation, path/encoding problems on a dead mount) = cannot prove deletion => temporary
            return false;
        }
    }

    // every failure increments the breaker (the caller has already negative-cached the item); the file system is touched
    // ONLY for a PermanentFailure while the circuit is closed and auto-delete is enabled
    private void HandleFailure(BaseItem item, FailureKind kind)
    {
        if (RecordFailure())
        {
            return;
        }

        if (kind != FailureKind.PermanentFailure || Plugin.Instance?.Configuration is not { EnableAutoDelete: true })
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await AutoHealDeadItemAsync(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "QuickSTRM: AutoHealDeadItemAsync failed for {Name}", item.Name);
            }
        });
    }

    /// <summary>
    /// Executes auto-heal deletion and *arr replacement for a confirmed dead (404) .strm item.
    /// Verifies mount health, deletes the local .strm file, removes the item from Jellyfin library,
    /// and invokes <see cref="OnStrmDeleted"/> to trigger *arr recovery.
    /// </summary>
    public async Task AutoHealDeadItemAsync(BaseItem item, CancellationToken ct = default)
    {
        if (Plugin.Instance?.Configuration is not { EnableAutoDelete: true })
        {
            _logger.LogInformation("QuickSTRM: Auto-delete disabled in configuration; skipping deletion of dead .strm link {Name}", item.Name);
            return;
        }

        if (!IsParentDirectoryReadable(item.Path))
        {
            _logger.LogWarning("QuickSTRM: Parent directory unreadable or mount marker missing for {Name}; treating failure as temporary (dropped mount)", item.Name);
            return;
        }

        try
        {
            var deletedPath = item.Path;
            if (deletedPath is not null && File.Exists(deletedPath))
            {
                File.Delete(deletedPath);
            }

            // remove from the Jellyfin library without touching disk again (file already deleted above)
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
            _logger.LogWarning("QuickSTRM: Auto-deleted dead .strm link {Name} ({Path})", item.Name, deletedPath);

            await _telemetryService.LogAutoHealAsync(deletedPath ?? item.Name, item.Name).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(deletedPath) && OnStrmDeleted is not null)
            {
                await OnStrmDeleted.Invoke(deletedPath).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuickSTRM: Auto-delete failed for {Name}", item.Name);
        }
    }

    private void MarkPersistFailed(BaseItem item)
    {
        // prevents perpetual lockouts from automated client retries: absolute expiration of exactly 1 hour is enforced instead
        // of a sliding window, so background client requests or automated retries cannot continuously postpone the retry opportunity
        _failedPersistCache[item.Id] = DateTime.UtcNow.Add(FailedPersistAbsoluteExpiration);

        _logger.LogWarning(
            "QuickSTRM: Background persist failed for {Name}; negative cache set with {Hours}h absolute expiration (prevents perpetual lockout)",
            item.Name,
            FailedPersistAbsoluteExpiration.TotalHours);
    }

    /// <summary>
    /// Sweeps expired entries from _resolvedUrlCache and _failedPersistCache to ensure bounded memory usage on large libraries.
    /// </summary>
    public void SweepExpiredCaches()
    {
        var now = DateTime.UtcNow;
        var sweptUrls = 0;
        var sweptFailed = 0;

        foreach (var kvp in _resolvedUrlCache)
        {
            if (now >= kvp.Value.ExpiresAt)
            {
                if (_resolvedUrlCache.TryRemove(kvp.Key, out _))
                {
                    sweptUrls++;
                }
            }
        }

        foreach (var kvp in _failedPersistCache)
        {
            if (now >= kvp.Value)
            {
                if (_failedPersistCache.TryRemove(kvp.Key, out _))
                {
                    sweptFailed++;
                }
            }
        }

        if (sweptUrls > 0 || sweptFailed > 0)
        {
            _logger.LogDebug(
                "QuickSTRM: Background cache sweep evicted {SweptUrls} expired URLs and {SweptFailed} expired negative cache entries",
                sweptUrls,
                sweptFailed);
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        try
        {
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                SweepExpiredCaches();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Error in background cache maintenance sweep");
        }
    }

    private async Task<MediaInfo?> ProbeOneInlineAsync(
        MediaSourceInfo source,
        CancellationToken budgetToken,
        CancellationToken ct,
        BaseItem? item = null)
    {
        var itemId = item?.Id ?? (Guid.TryParse(source.Id, out var parsedId) ? parsedId : Guid.Empty);
        if (itemId != Guid.Empty && _inflightProbes.TryGetValue(itemId, out var existingTask))
        {
            _logger.LogInformation("QuickSTRM: Awaiting existing in-flight probe for inline item {Id}", itemId);
            try
            {
                await existingTask.ConfigureAwait(false);
            }
            catch
            {
            }
            return null;
        }

        TaskCompletionSource<bool>? inlineTcs = null;
        if (itemId != Guid.Empty)
        {
            inlineTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inflightProbes.TryAdd(itemId, inlineTcs.Task))
            {
                if (_inflightProbes.TryGetValue(itemId, out var racedTask))
                {
                    try
                    {
                        await racedTask.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                    return null;
                }
            }
        }

        MediaInfo? mediaInfo = null;
        try
        {
            // Resolve target path if source.Path points to a .strm file
            var targetPath = source.Path;
            if (!string.IsNullOrEmpty(targetPath) && targetPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var resolvedUrl = await ReadStrmUrlAsync(targetPath, budgetToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(resolvedUrl))
                    {
                        targetPath = resolvedUrl;
                    }
                }
                catch (Exception)
                {
                }
            }

            // 1. If target local path does not exist yet: hold-open retry loop
            if (!string.IsNullOrEmpty(targetPath) && Path.IsPathRooted(targetPath) && !targetPath.Contains("://", StringComparison.Ordinal))
            {
                if (!File.Exists(targetPath))
                {
                    var exists = false;
                    foreach (var delay in HoldOpenRetryDelays)
                    {
                        await Task.Delay(delay, budgetToken).ConfigureAwait(false);
                        if (File.Exists(targetPath))
                        {
                            exists = true;
                            break;
                        }
                    }

                    if (!exists)
                    {
                        _logger.LogWarning("QuickSTRM: Inline probe target local path does not exist after hold-open retries: {Path}", targetPath);
                        var itemToFail = item ?? (Guid.TryParse(source.Id, out var id) ? _libraryManager.GetItemById(id) : null);
                        if (itemToFail is not null)
                        {
                            MarkPersistFailed(itemToFail);
                            HandleFailure(itemToFail, FailureKind.TemporaryFailure);
                        }
                        return null;
                    }
                }
            }
            // 2. If target is HTTP: verify readiness before acquiring probe slot
            else if (!string.IsNullOrEmpty(targetPath) && Uri.TryCreate(targetPath, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var httpResult = await CheckHttpEndpointWithHoldOpenRetryAsync(uri, budgetToken).ConfigureAwait(false);
                if (!httpResult.IsHealthy)
                {
                    _logger.LogWarning("QuickSTRM: Inline probe target HTTP endpoint dead after hold-open retries: {Uri} ({Status})", uri, httpResult.StatusCode);
                    var itemToFail = item ?? (Guid.TryParse(source.Id, out var id) ? _libraryManager.GetItemById(id) : null);
                    if (itemToFail is not null)
                    {
                        MarkPersistFailed(itemToFail);
                        HandleFailure(itemToFail, httpResult.FailureKind);
                    }
                    return null;
                }
            }

            try
            {
                // protects low-end CPUs: shares the same 2-slot gate as background probes; the budget also covers the slot wait
                await _probeGate.WaitAsync(budgetToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linkedBudget = CancellationTokenSource.CreateLinkedTokenSource(budgetToken, cts.Token);

            try
            {
                // improves latency: ffprobe only, no provider lookups or DB writes, so the budget is spent on the network probe
                mediaInfo = await _mediaEncoder.GetMediaInfo(
                    new MediaInfoRequest
                    {
                        MediaSource = source,
                        MediaType = DlnaProfileType.Video,
                        ExtractChapters = false
                    },
                    linkedBudget.Token).ConfigureAwait(false);
                return mediaInfo;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("QuickSTRM: Hardened FFprobe killswitch triggered after 8s for inline source {Id}", source.Id);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // protects playback: one source's failure or timeout yields null for that source only; the others still resolve
                if (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "QuickSTRM: Inline probe failed for source {Id}", source.Id);
                }

                return null;
            }
            finally
            {
                ReleaseQuietly(_probeGate);
            }
        }
        finally
        {
            if (itemId != Guid.Empty && inlineTcs is not null)
            {
                _inflightProbes.TryRemove(itemId, out _);
                inlineTcs.TrySetResult(mediaInfo != null);
            }
        }
    }

    /// <summary>
    /// Initiates a non-blocking background library scan and enqueues matching .strm items into the persist channel.
    /// Returns immediately without blocking the HTTP request thread.
    /// </summary>
    public Task QueueLibraryScanAsync(ProbeMode mode)
    {
        Interlocked.Exchange(ref _isBatchRunning, 1);
        if (Interlocked.CompareExchange(ref _isScanningLibrary, 1, 0) != 0)
        {
            _logger.LogInformation("QuickSTRM: Library scan is already running; skipping duplicate scan request");
            return Task.CompletedTask;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ScanAndQueueLibraryAsync(mode).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QuickSTRM: Background library scan failed");
            }
            finally
            {
                Interlocked.Exchange(ref _isScanningLibrary, 0);
                if (Volatile.Read(ref _activeProbes) == 0 &&
                    _highPriorityChannel.Reader.Count == 0 &&
                    _lowPriorityChannel.Reader.Count == 0)
                {
                    Interlocked.Exchange(ref _isBatchRunning, 0);
                }
            }
        });

        return Task.CompletedTask;
    }

    private async Task ScanAndQueueLibraryAsync(ProbeMode mode)
    {
        var isIncremental = mode == ProbeMode.Incremental || mode == ProbeMode.OnlyUnprobed;
        var offset = 0;
        const int pageSize = 500;
        var enqueuedCount = 0;

        _logger.LogInformation("QuickSTRM: Starting background library scan for {Mode} probe", mode);

        while (!_shutdownCts.IsCancellationRequested && Volatile.Read(ref _isScanningLibrary) == 1)
        {
            var query = new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video },
                IsVirtualItem = false,
                Recursive = true,
                StartIndex = offset,
                Limit = pageSize
            };

            var batch = _libraryManager.GetItemList(query);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var item in batch)
            {
                if (item.Path is not null && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                {
                    if (isIncremental)
                    {
                        var hasRuntime = item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0;
                        var hasDimensions = item.Width > 0 && item.Height > 0;

                        // In Jellyfin, probed items already have RunTimeTicks or dimensions populated on BaseItem
                        if (hasRuntime || hasDimensions)
                        {
                            continue;
                        }

                        // Only call _mediaSourceManager.GetMediaStreams(item.Id) if those properties are missing
                        if (_mediaSourceManager != null)
                        {
                            var streams = _mediaSourceManager.GetMediaStreams(item.Id);
                            if (streams != null && streams.Count > 0)
                            {
                                continue;
                            }
                        }
                    }

                    if (EnqueueBackgroundPersist(item, highPriority: false, force: !isIncremental))
                    {
                        enqueuedCount++;
                    }
                }
            }

            offset += batch.Count;
            if (batch.Count < pageSize)
            {
                break;
            }

            try
            {
                await Task.Delay(5, _shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation(
            "QuickSTRM: Background library scan completed for {Mode} probe; enqueued {Count} items into low-priority channel",
            mode,
            enqueuedCount);
    }

    /// <summary>
    /// Starts a probe task. Returns false if one is already running.
    /// </summary>
    /// <param name="mode">"Incremental" or "Full".</param>
    public bool StartProbe(string mode)
    {
        lock (_lock)
        {
            if (State == "Running")
            {
                return false;
            }

            var items = GetStrmItems();
            if (string.Equals(mode, "Incremental", StringComparison.OrdinalIgnoreCase))
            {
                items = items.Where(i => !HasStreamInfo(i)).ToList();
            }

            TotalItems = items.Count;
            ProcessedItems = 0;
            CurrentItemName = string.Empty;
            State = "Running";

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _probeTask = Task.Run(() => RunProbeAsync(items, ct));
            return true;
        }
    }

    /// <summary>
    /// Requests cancellation of the running probe task.
    /// </summary>
    public void StopProbe()
    {
        Interlocked.Exchange(ref _isScanningLibrary, 0);
        Interlocked.Exchange(ref _isBatchRunning, 0);
        lock (_lock)
        {
            if (_cts is { IsCancellationRequested: false })
            {
                State = "Stopping";
                _cts.Cancel();
            }
        }
    }

    // ── Internal ──

    private async Task RunProbeAsync(List<BaseItem> items, CancellationToken ct)
    {
        try
        {
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (_circuitTripped)
                {
                    _logger.LogCritical("QuickSTRM: Circuit breaker tripped; halting bulk probe");
                    break;
                }

                CurrentItemName = item.Name ?? item.Path ?? string.Empty;
                _logger.LogInformation("QuickSTRM: Probing [{Index}/{Total}] {Name}",
                    ProcessedItems + 1, TotalItems, CurrentItemName);

                try
                {
                    await ProbeItemAsync(item, BackgroundProbeTimeout, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "QuickSTRM: Probe failed for {Name}, skipping", CurrentItemName);
                }

                ProcessedItems++;
            }
        }
        finally
        {
            lock (_lock)
            {
                State = "Idle";
                CurrentItemName = string.Empty;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    private sealed class TrialState
    {
        /// <summary>null = undecided (cancelled/refused): the trial lock is released without touching breaker state.</summary>
        public bool? Success;
    }

    /// <summary>Result of a probe attempt, distinguishing "nothing was attempted" from a real failure.</summary>
    private enum ProbeAttempt
    {
        /// <summary>The refresh ran (stream info must still be verified by the caller).</summary>
        Completed,

        /// <summary>The attempt failed (pre-flight failure, timeout, no slot): already counted/cached where appropriate.</summary>
        Failed,

        /// <summary>Refused because the breaker is open or another request holds the half-open trial: never counted, cached or deleted.</summary>
        SkippedDueToTrial
    }

    /// <summary>
    /// Triggers the official Jellyfin metadata refresh with remote content probing
    /// for a single item. This writes real probe data into the database.
    /// Circuit-aware entry point: the half-open trial lock, if taken here, is held for the ENTIRE pipeline (HEAD pre-flight,
    /// _probeGate wait, ffprobe) and released in a finally block, so an unhandled exception can never deadlock the breaker.
    /// The circuit closes only when ffprobe actually produced stream info. Requests refused by an open breaker or an active
    /// trial return <see cref="ProbeAttempt.SkippedDueToTrial"/> and bypass the whole failure pipeline.
    /// </summary>
    private async Task<ProbeAttempt> ProbeItemAsync(
        BaseItem item,
        TimeSpan timeout,
        CancellationToken ct,
        Action? onSlotAcquired = null,
        Action? onSlotReleased = null)
    {
        if (!TryEnterCircuit(out var isTrial))
        {
            return ProbeAttempt.SkippedDueToTrial;
        }

        var trial = new TrialState();
        try
        {
            var completed = await ProbeItemCoreAsync(item, timeout, ct, onSlotAcquired, onSlotReleased, trial).ConfigureAwait(false);
            return completed ? ProbeAttempt.Completed : ProbeAttempt.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // unhandled probe-stage error during a trial = failed trial (the lock is still released below)
            trial.Success ??= false;
            throw;
        }
        finally
        {
            if (isTrial)
            {
                if (trial.Success.HasValue)
                {
                    ResolveTrial(trial.Success.Value);
                }
                else
                {
                    Interlocked.Exchange(ref _halfOpenInFlight, 0);
                }
            }
        }
    }

    private async Task<bool> ProbeItemCoreAsync(
        BaseItem item,
        TimeSpan timeout,
        CancellationToken ct,
        Action? onSlotAcquired,
        Action? onSlotReleased,
        TrialState trial)
    {
        if (item.Id != Guid.Empty && _inflightProbes.TryGetValue(item.Id, out var existingTask))
        {
            _logger.LogInformation("QuickSTRM: In-flight probe coalesced for item {Name} ({Id})", item.Name, item.Id);
            try
            {
                return await existingTask.ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }

        TaskCompletionSource<bool>? probeTcs = null;
        if (item.Id != Guid.Empty)
        {
            probeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inflightProbes.TryAdd(item.Id, probeTcs.Task))
            {
                if (_inflightProbes.TryGetValue(item.Id, out var racedTask))
                {
                    try
                    {
                        return await racedTask.ConfigureAwait(false);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        var probeOk = false;
        try
        {
            // protects low-end CPUs: only background work uses the heavy RefreshMetadata path; playback never waits on it
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // cheap HEAD check BEFORE waiting for / holding a gate slot, so dead links never occupy one of the 2 ffprobe slots;
            // it passes the trial forward without changing the circuit state - only a full ffprobe success may close it
            if (!await PreflightCoreAsync(item, ct).ConfigureAwait(false))
            {
                trial.Success = false;
                return false;
            }

            try
            {
                await _probeGate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                // lets the persistence queue distinguish items waiting for a slot from items actually probing (inside the try so the
                // matching release below always runs, even if this callback were ever to throw)
                onSlotAcquired?.Invoke();
                // protects bulk imports: the 60s clock starts only once we hold a slot, so queue time can't cause false timeouts
                timeoutCts.CancelAfter(timeout);

                // Hardened FFprobe Killswitch: 8-second strict timeout on probe execution
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cts.Token);

                // NOTE: this plugin does not launch ffprobe itself. RefreshMetadata -> Jellyfin's IMediaEncoder starts the process
                // and ties it to this token. We cancel the token rather than holding a Process handle we do not own.
                await item.RefreshMetadata(
                    new MetadataRefreshOptions(_directoryService)
                    {
                        EnableRemoteContentProbe = true,
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh
                    },
                    linkedCts.Token).ConfigureAwait(false);
                probeOk = HasStreamInfo(item);
                if (probeOk)
                {
                    ApplyTrickplayPrevention(item);
                }
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Hardened FFprobe Killswitch: treat link as dead/unreachable, abort database save
                _logger.LogWarning("QuickSTRM: Hardened FFprobe killswitch triggered after 8s for {Name}; treating link as dead/unreachable and aborting DB save", item.Name);
                return false;
            }
            finally
            {
                // protects low-end CPUs: always release the slot (even on error/cancel) so the 2-slot limit can't leak shut
                onSlotReleased?.Invoke();
                ReleaseQuietly(_probeGate);

                // slot is already free here, so failure handling (file/library deletion) never holds an ffprobe slot; it is fully guarded so
                // a fault here can never mask the real result or escape the pipeline
                try
                {
                    if (probeOk)
                    {
                        RecordSuccess();
                        trial.Success = true; // populated stream info = full end-to-end success
                    }
                    else if (!ct.IsCancellationRequested)
                    {
                        // timeouts, exceptions and empty/invalid ffprobe results are always temporary: never deletes anything
                        trial.Success = false;
                        MarkPersistFailed(item);
                        HandleFailure(item, FailureKind.TemporaryFailure);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "QuickSTRM: Post-probe bookkeeping failed for {Name}", item.Name);
                }
            }
        }
        finally
        {
            if (item.Id != Guid.Empty)
            {
                _inflightProbes.TryRemove(item.Id, out _);
                probeTcs?.TrySetResult(probeOk);
            }
        }
    }

    public List<BaseItem> GetStrmItems()
    {
        return _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video },
                Recursive = true
            })
            .Where(IsStrmItem)
            .ToList();
    }

    private static bool IsStrmItem(BaseItem item)
        => item.Path is not null
           && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    private static bool HasStreamInfo(BaseItem item)
        => item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0;

    // a slot holder finishing after Dispose must not throw ObjectDisposedException out of a finally block
    private static void ReleaseQuietly(SemaphoreSlim gate)
    {
        try
        {
            gate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _libraryManager.ItemAdded -= OnItemAdded;

        // 1) cancel first so in-flight HEADs, queued probes and ffprobe-owning refreshes stop before their gates disappear
        try
        {
            _shutdownCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _cts?.Dispose();

        _failedPersistCache.Clear();
        _inflightProbes.Clear();
        _pendingPersist.Clear();
        _resolvedUrlCache.Clear();

        // 3) dispose what we own
        _preflightClient.Dispose();
        _probeGate.Dispose();
        _headGate.Dispose();
        _shutdownCts.Dispose();
    }

    private void ApplyTrickplayPrevention(BaseItem item)
    {
        try
        {
            // Explicitly set item.EnableTrickplay = false
            var prop = item.GetType().GetProperty("EnableTrickplay", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(item, false);
            }
            else if (item.ProviderIds != null)
            {
                item.ProviderIds["EnableTrickplay"] = "false";
            }

            // Ensure any existing .bif metadata attachments are purged from the item's metadata dictionary
            if (item.ProviderIds != null)
            {
                item.ProviderIds.Remove("bif");
                item.ProviderIds.Remove("Bif");
                item.ProviderIds.Remove("trickplay");
                item.ProviderIds.Remove("Trickplay");
                item.ProviderIds.Remove("BifAttachment");
            }

            // Call _itemRepository.UpdateItem to persist this protection lock
            _itemRepository.UpdateItem(item);

            _logger.LogDebug("QuickSTRM: Trickplay prevention lock applied for {Name}", item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Trickplay prevention lock failed for {Name}", item.Name);
        }
    }
}

/// <summary>
/// Extension methods for <see cref="IItemRepository"/> to persist item metadata updates.
/// </summary>
public static class ItemRepositoryExtensions
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo?> CachedUpdateMethods = new();

    /// <summary>
    /// Persists item metadata changes to the repository or database.
    /// </summary>
    public static void UpdateItem(this IItemRepository? repository, BaseItem item)
    {
        if (repository is not null)
        {
            var repoType = repository.GetType();
            var updateMethod = CachedUpdateMethods.GetOrAdd(repoType, t =>
                t.GetMethod("UpdateItem", new[] { typeof(BaseItem) })
                ?? t.GetMethod("UpdateItem", BindingFlags.Public | BindingFlags.Instance));

            if (updateMethod is not null)
            {
                updateMethod.Invoke(repository, new object[] { item });
                return;
            }
        }

        item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
    }
}
