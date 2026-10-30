using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
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
using System.Runtime.CompilerServices;

namespace QuickSTRM;

/// <summary>
/// Exception thrown when FFprobe encounters an unrecoverable probing error, format failure, or returns no streams.
/// </summary>
public class FfmpegException : Exception
{
    public FfmpegException(string message) : base(message) { }
    public FfmpegException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Operational mode for manual library pre-probing.
/// </summary>
public enum ProbeMode
{
    Incremental,
    All,
    OnlyUnprobed = Incremental,
    FullRefresh = All
}

/// <summary>
/// Thread-safe in-memory cache record containing probed media stream descriptors,
/// container format, bitrate, size, and runtime ticks for an item.
/// </summary>
public sealed class CachedProbeResult
{
    public required IReadOnlyList<MediaStream> MediaStreams { get; init; }
    public long? RunTimeTicks { get; init; }
    public long? Bitrate { get; init; }
    public long? Size { get; init; }
    public string? Container { get; init; }
    public DateTime ProbedAtUtc { get; init; } = DateTime.UtcNow;
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task<MediaInfo?>> _inflightProbes = new();

    // Concurrency throttle preventing more than 4 concurrent ffprobe streams against Usenet sockets / Kestrel thread pool
    private readonly SemaphoreSlim _probeThrottle = new(4, 4);

    // Active probe item names currently inside the probe throttle
    private readonly ConcurrentDictionary<Guid, string> _activeProbeNames = new();

    /// <summary>Gets the names of items currently being actively probed inside the concurrency throttle.</summary>
    public IReadOnlyCollection<string> ActiveProbeNames => _activeProbeNames.Values.ToArray();

    // Consecutive failure retry count per item across probe passes (3-strike state machine)
    private readonly ConcurrentDictionary<Guid, int> _itemRetryCounts = new();

    /// <summary>Gets the current consecutive failure retry counts for items.</summary>
    public IReadOnlyDictionary<Guid, int> ItemRetryCounts => _itemRetryCounts;

    // Staged backlog queue for multi-batch library scans exceeding MaxPendingPersist (500)
    private readonly ConcurrentQueue<(BaseItem Item, bool Force)> _stagedBatchQueue = new();
    private readonly ConcurrentDictionary<Guid, byte> _stagedItemIds = new();
    private readonly object _replenishLock = new();

    /// <summary>Gets the number of items staged in the multi-batch continuation backlog.</summary>
    public int StagedQueueCount => _stagedItemIds.Count;

    private readonly Channel<BaseItem> _highPriorityChannel = Channel.CreateBounded<BaseItem>(
        new BoundedChannelOptions(MaxPendingPersist) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<BaseItem> _lowPriorityChannel = Channel.CreateBounded<BaseItem>(
        new BoundedChannelOptions(MaxPendingPersist) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly List<Task> _workerTasks = new();
    private readonly object _workerLock = new();
    private int _activeConsumerWorkers;
    private readonly IItemRepository? _itemRepository;

    /// <summary>
    /// Ensures consumer worker tasks are active and processing the probe queue.
    /// </summary>
    public void EnsureConsumerRunning()
    {
        if (_shutdownCts.IsCancellationRequested || Volatile.Read(ref _activeConsumerWorkers) >= MaxConcurrentProbes)
        {
            return;
        }

        lock (_workerLock)
        {
            _workerTasks.RemoveAll(static t => t.IsCompleted);
            while (Volatile.Read(ref _activeConsumerWorkers) < MaxConcurrentProbes)
            {
                Interlocked.Increment(ref _activeConsumerWorkers);
                _workerTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ProcessQueueLoopAsync(_shutdownCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeConsumerWorkers);
                    }
                }));
            }
        }
    }

    /// <summary>
    /// Triggers immediate queue draining by ensuring consumer loops are active.
    /// </summary>
    public void TriggerQueueProcessing() => EnsureConsumerRunning();

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
    public int PersistQueueWaiting => HighPriorityQueueWaiting + LowPriorityQueueWaiting + StagedQueueCount;

    /// <summary>Gets the total number of items currently waiting in the persistence queues.</summary>
    public int QueueCount => PersistQueueWaiting;

    private readonly ConcurrentDictionary<Guid, IReadOnlyList<MediaStream>> _inMemoryStreams = new();

    /// <summary>Gets in-memory cached media stream descriptors keyed by item ID.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<MediaStream>> InMemoryStreams => _inMemoryStreams;

    private readonly ConcurrentDictionary<Guid, CachedProbeResult> _probeResultCache = new();

    /// <summary>Gets thread-safe in-memory cached probe results keyed by item ID.</summary>
    public IReadOnlyDictionary<Guid, CachedProbeResult> ProbeResultCache => _probeResultCache;

    /// <summary>
    /// Attempts to retrieve a cached probe result for the given item ID.
    /// </summary>
    public bool TryGetCachedProbeResult(Guid itemId, out CachedProbeResult? result)
        => _probeResultCache.TryGetValue(itemId, out result);

    internal void ClearProbeResultCache() => _probeResultCache.Clear();

    /// <summary>
    /// Cancels any active in-flight prewarm stream for the specified item ID.
    /// </summary>
    public void CancelPrewarm(Guid itemId)
    {
        if (itemId != Guid.Empty)
        {
            _vfsPrewarmService?.CancelPrewarm(itemId);
        }
    }

    /// <summary>Gets the number of queued items currently being persisted (holding a probe slot).</summary>
    public int PersistQueueRunning => ActiveProbes;

    /// <summary>Gets the total number of items queued or running for background persistence.</summary>
    public int PersistQueueTotal => _pendingPersist.Count;

    /// <summary>Gets the number of items currently in the negative failure cache.</summary>
    public int FailureCacheCount => _failedPersistCache.Count;

    // protects playback & socket starvation: hard ceiling of 4 concurrent ffprobe-backed refreshes across ALL callers
    public const int MaxConcurrentProbes = 4;
    private readonly SemaphoreSlim _probeGate = new(MaxConcurrentProbes, MaxConcurrentProbes);

    // protects bulk imports: generous 60s lets slow cloud-drive links finish without failing the library scan
    private static readonly TimeSpan BackgroundProbeTimeout = TimeSpan.FromSeconds(60);

    // protects connected clients: inline (playback-time) probes allow hold-open retry window (~8s) plus probe execution
    private static readonly TimeSpan InlineProbeTimeout = TimeSpan.FromSeconds(8);

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

    // pre-flight HEAD must answer within 500ms or the link is treated as dead, so no ffprobe slot is spent on it
    private static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(3);
    // Singleton or injected client backed by specialized SocketsHttpHandler connection pool
    private readonly HttpClient _preflightClient;

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
    private readonly AutoHealService? _autoHealService;
    private readonly VfsPrewarmService? _vfsPrewarmService;
    private readonly IMediaStreamRepository? _mediaStreamRepository;

    public ProbeService(
        ILibraryManager libraryManager,
        IDirectoryService directoryService,
        IMediaEncoder mediaEncoder,
        IMemoryCache? memoryCache = null,
        IActivityManager activityManager = null!,
        ILogger<ProbeService> logger = null!,
        TelemetryService telemetryService = null!,
        IItemRepository? itemRepository = null,
        IMediaSourceManager? mediaSourceManager = null,
        AutoHealService? autoHealService = null,
        HttpClient? httpClient = null,
        VfsPrewarmService? vfsPrewarmService = null,
        IMediaStreamRepository? mediaStreamRepository = null)
    {
        _telemetryService = telemetryService;
        _libraryManager = libraryManager;
        _directoryService = directoryService;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _itemRepository = itemRepository;
        _mediaSourceManager = mediaSourceManager;
        _autoHealService = autoHealService;
        _preflightClient = httpClient ?? Plugin.HttpClient;
        _vfsPrewarmService = vfsPrewarmService;
        _mediaStreamRepository = mediaStreamRepository;
    }

    // ── IHostedService lifecycle ──

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAddedOrUpdated;
        _libraryManager.ItemUpdated += OnItemAddedOrUpdated;

        EnsureConsumerRunning();

        // Launch background maintenance loop (periodic cache sweep every 10 minutes)
        lock (_workerLock)
        {
            _workerTasks.Add(Task.Run(() => MaintenanceLoopAsync(_shutdownCts.Token)));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAddedOrUpdated;
        _libraryManager.ItemUpdated -= OnItemAddedOrUpdated;
        _shutdownCts.Cancel();
        _highPriorityChannel.Writer.TryComplete();
        _lowPriorityChannel.Writer.TryComplete();
        StopProbe();

        List<Task> tasksToWait;
        lock (_workerLock)
        {
            tasksToWait = new List<Task>(_workerTasks);
        }

        try
        {
            await Task.WhenAll(tasksToWait).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    // ── Auto-probe on item added ──

    private void OnItemAddedOrUpdated(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is { EnableAutoProbe: false })
        {
            return;
        }

        if (e.Item is not Video item || !IsStrmFile(item) || HasStreamInfo(item))
        {
            return;
        }

        // protects low-end CPUs and memory: routed through the SAME bounded (500), de-duplicated, negative-cached queue as playback
        // persistence, so a bulk import can no longer spawn one unbounded parked Task per added item; the 2-slot gate still applies
        // Strictly non-blocking: routed through the bounded, deduplicated, negative-cached background channel
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

        // protects memory: bounded queue ceiling across both lanes; overflow items are staged for sequential batch continuation
        if (_pendingPersist.Count >= MaxPendingPersist)
        {
            return StageForNextBatch(item, force);
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
            return StageForNextBatch(item, force);
        }

        Interlocked.Exchange(ref _isBatchRunning, 1);
        EnsureConsumerRunning();
        return true;
    }

    private bool StageForNextBatch(BaseItem item, bool force)
    {
        if (_pendingPersist.ContainsKey(item.Id))
        {
            return false;
        }

        if (!_stagedItemIds.TryAdd(item.Id, 0))
        {
            return false;
        }

        _stagedBatchQueue.Enqueue((item, force));
        Interlocked.Exchange(ref _isBatchRunning, 1);
        return true;
    }

    private void ReplenishFromStagedBatches()
    {
        if (_stagedBatchQueue.IsEmpty || _shutdownCts.IsCancellationRequested)
        {
            return;
        }

        if (_circuitTripped && (CircuitCooldownRemaining > TimeSpan.Zero || Volatile.Read(ref _halfOpenInFlight) != 0))
        {
            return;
        }

        lock (_replenishLock)
        {
            var replenished = 0;
            while (_pendingPersist.Count < MaxPendingPersist && _stagedBatchQueue.TryDequeue(out var staged))
            {
                _stagedItemIds.TryRemove(staged.Item.Id, out _);

                if (!staged.Force && HasStreamInfo(staged.Item))
                {
                    continue;
                }

                if (_failedPersistCache.TryGetValue(staged.Item.Id, out var expiresAt))
                {
                    if (expiresAt > DateTime.UtcNow)
                    {
                        continue;
                    }
                    _failedPersistCache.TryRemove(staged.Item.Id, out _);
                }

                if (!_pendingPersist.TryAdd(staged.Item.Id, 0))
                {
                    continue;
                }

                if (!_lowPriorityChannel.Writer.TryWrite(staged.Item))
                {
                    _pendingPersist.TryRemove(staged.Item.Id, out _);
                    if (_stagedItemIds.TryAdd(staged.Item.Id, 0))
                    {
                        _stagedBatchQueue.Enqueue(staged);
                    }
                    break;
                }

                replenished++;
            }

            if (replenished > 0)
            {
                Interlocked.Exchange(ref _isBatchRunning, 1);
                _logger.LogInformation(
                    "QuickSTRM: Queued next batch of {Replenished} items from backlog ({Remaining} staged items remaining)",
                    replenished,
                    _stagedItemIds.Count);
            }
        }
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
        EnsureConsumerRunning();
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
                if (!_stagedBatchQueue.IsEmpty)
                {
                    ReplenishFromStagedBatches();
                    if (_highPriorityChannel.Reader.TryRead(out item) || _lowPriorityChannel.Reader.TryRead(out item))
                    {
                        goto DispatchItem;
                    }
                }

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

            DispatchItem:
            if (item is null)
            {
                continue;
            }

            if (!TryEnterCircuit(out var isTrial))
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await _probeThrottle.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _pendingPersist.TryRemove(item.Id, out _);
                break;
            }

            Interlocked.Increment(ref _activeProbes);
            var itemName = item.Name ?? item.Path ?? "Unknown";
            _activeProbeNames[item.Id] = itemName;

            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var mediaInfo = await ExecuteCoreProbeAsync(
                            item,
                            BackgroundProbeTimeout,
                            ct).ConfigureAwait(false);

                        if (isTrial)
                        {
                            ResolveTrial(mediaInfo is not null && HasStreamInfo(item));
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "QuickSTRM: Background persist failed for {Name}", item.Name);
                        if (isTrial)
                        {
                            ResolveTrial(false);
                        }
                    }
                    finally
                    {
                        _activeProbeNames.TryRemove(item.Id, out _);
                        _pendingPersist.TryRemove(item.Id, out _);
                        Interlocked.Decrement(ref _activeProbes);
                        ReleaseQuietly(_probeThrottle);
                        if (_lowPriorityChannel.Reader.Count == 0 && !_stagedBatchQueue.IsEmpty)
                        {
                            ReplenishFromStagedBatches();
                        }
                        if (Volatile.Read(ref _activeProbes) == 0 &&
                            _highPriorityChannel.Reader.Count == 0 &&
                            _lowPriorityChannel.Reader.Count == 0 &&
                            _stagedBatchQueue.IsEmpty &&
                            Volatile.Read(ref _isScanningLibrary) == 0)
                        {
                            Interlocked.Exchange(ref _isBatchRunning, 0);
                        }
                    }
                }, CancellationToken.None);
            }
            catch (Exception)
            {
                _activeProbeNames.TryRemove(item.Id, out _);
                _pendingPersist.TryRemove(item.Id, out _);
                Interlocked.Decrement(ref _activeProbes);
                ReleaseQuietly(_probeThrottle);
                throw;
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
    /// Flushes the static resolved URL cache.
    /// </summary>
    public static void ClearResolvedUrlCache()
    {
        _resolvedUrlCache.Clear();
    }

    /// <summary>
    /// Flushes the negative failure cache, allowing previously failed items to be retried immediately.
    /// </summary>
    public void ClearFailureCache()
    {
        _failedPersistCache.Clear();
        _resolvedUrlCache.Clear();
        _itemRetryCounts.Clear();
        ResetCircuitBreaker();
        _logger.LogInformation("QuickSTRM: Flushed negative persistence failure cache, resolved URL cache, and retry counts");
    }

    /// <summary>Manual override: closes the breaker immediately, zeroes the failure counter, and clears the failure cache.</summary>
    public void ResetCircuitBreaker()
    {
        lock (_circuitLock)
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            Interlocked.Exchange(ref _circuitTrippedAtTicks, 0);
            Interlocked.Exchange(ref _halfOpenInFlight, 0);
            _failedPersistCache.Clear();
            if (_circuitTripped)
            {
                _circuitTripped = false;
                _logger.LogInformation("QuickSTRM: Circuit breaker manually reset to closed");
                _ = _telemetryService?.LogCircuitBreakerAsync("Reset", "Circuit breaker manually reset to closed.");
            }
        }
    }

    internal void TripCircuitBreakerForTesting()
    {
        lock (_circuitLock)
        {
            Interlocked.Exchange(ref _consecutiveFailures, 20);
            Interlocked.Exchange(ref _circuitTrippedAtTicks, DateTime.UtcNow.Ticks);
            _circuitTripped = true;
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
                    _ = _telemetryService?.LogCircuitBreakerAsync(
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
                    _ = _telemetryService?.LogCircuitBreakerAsync("Reset", "Half-open trial succeeded; circuit breaker closed, queue processing resumed.");
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
        _ = _telemetryService?.LogEventAsync("QuickSTRM", message);
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
    internal static TimeSpan[] HoldOpenRetryDelays =
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

    internal static void CacheResolvedUrl(string url, string resolvedUrl)
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
    internal static HttpClient _redirectResolutionClient = Plugin.HttpClient;

    /// <summary>
    /// Parses the first valid non-empty .strm line from a raw UTF-8 byte span into destination chars without heap allocations.
    /// </summary>
    public static bool TryExtractFirstStrmLine(ReadOnlySpan<byte> utf8Bytes, Span<char> destination, out int charsWritten)
    {
        // Strip UTF-8 BOM if present (0xEF, 0xBB, 0xBF)
        if (utf8Bytes.Length >= 3 && utf8Bytes[0] == 0xEF && utf8Bytes[1] == 0xBB && utf8Bytes[2] == 0xBF)
        {
            utf8Bytes = utf8Bytes.Slice(3);
        }

        while (!utf8Bytes.IsEmpty)
        {
            int lineEnd = utf8Bytes.IndexOfAny((byte)'\r', (byte)'\n');
            ReadOnlySpan<byte> lineBytes = lineEnd >= 0 ? utf8Bytes.Slice(0, lineEnd) : utf8Bytes;

            if (lineEnd >= 0)
            {
                utf8Bytes = utf8Bytes.Slice(lineEnd);
                while (!utf8Bytes.IsEmpty && (utf8Bytes[0] == (byte)'\r' || utf8Bytes[0] == (byte)'\n'))
                {
                    utf8Bytes = utf8Bytes.Slice(1);
                }
            }
            else
            {
                utf8Bytes = ReadOnlySpan<byte>.Empty;
            }

            lineBytes = lineBytes.Trim((byte)' ');
            lineBytes = lineBytes.Trim((byte)'\t');
            lineBytes = lineBytes.Trim((byte)'\r');
            lineBytes = lineBytes.Trim((byte)'\n');

            if (lineBytes.IsEmpty)
            {
                continue;
            }

            return Encoding.UTF8.TryGetChars(lineBytes, destination, out charsWritten);
        }

        charsWritten = 0;
        return false;
    }

    /// <summary>
    /// Synchronously reads the first non-empty line of a .strm handle into a stack-allocated or rented buffer with zero heap allocations.
    /// </summary>
    public static int ReadStrmLineZeroAlloc(SafeFileHandle handle, Span<byte> byteBuffer, Span<char> charDestination)
    {
        var bytesRead = RandomAccess.Read(handle, byteBuffer, 0);
        if (bytesRead <= 0)
        {
            return 0;
        }

        if (TryExtractFirstStrmLine(byteBuffer.Slice(0, bytesRead), charDestination, out var charsWritten))
        {
            return charsWritten;
        }

        return 0;
    }

    // Lock-free coalescing for concurrent requests on the same .strm file
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflightFileReads = new(StringComparer.OrdinalIgnoreCase);
    internal static int FileReadExecutionCount;

    /// <summary>
    /// Reads only the first non-empty line of a .strm file up to 4096 characters,
    /// avoiding large object heap (LOH) allocations on corrupted or binary files.
    /// Executes a lightweight HTTP HEAD request with User-Agent: Lavf/60.3.100 and AllowAutoRedirect = false
    /// to pre-resolve 301, 302, 307, and 308 redirects, falling back to GET Range: bytes=0-0 on 405/403,
    /// with a 15-minute cache TTL and a hold-open retry loop for newly added / mounting targets.
    /// Implements lock-free thundering-herd coalescing using ConcurrentDictionary and Lazy&lt;Task&lt;string?&gt;&gt;.
    /// </summary>
    public static async Task<string?> ReadStrmUrlAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var lazy = _inflightFileReads.GetOrAdd(
            path,
            p => new Lazy<Task<string?>>(() => ReadStrmUrlCoreAsync(p, CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var task = lazy.Value;
            var result = (!ct.CanBeCanceled || task.IsCompleted)
                ? await task.ConfigureAwait(false)
                : await task.WaitAsync(ct).ConfigureAwait(false);
            // Evict after burst window to coalesce high-volume thundering herds
            _ = Task.Delay(1000).ContinueWith(_ =>
            {
                ((ICollection<KeyValuePair<string, Lazy<Task<string?>>>>)_inflightFileReads)
                    .Remove(new KeyValuePair<string, Lazy<Task<string?>>>(path, lazy));
            }, TaskScheduler.Default);
            return result;
        }
        catch
        {
            ((ICollection<KeyValuePair<string, Lazy<Task<string?>>>>)_inflightFileReads)
                .Remove(new KeyValuePair<string, Lazy<Task<string?>>>(path, lazy));
            throw;
        }
    }

    /// <summary>
    /// Fast, non-blocking synchronous .strm target URL reader for latency-critical HTTP Range requests (/Videos/{id}/stream).
    /// Reads from in-memory caches or directly from disk without issuing any network HTTP HEAD requests.
    /// </summary>
    public static string? ReadStrmTargetFast(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (_inflightFileReads.TryGetValue(path, out var existingLazy) &&
            existingLazy.IsValueCreated &&
            existingLazy.Value.IsCompletedSuccessfully)
        {
            return existingLazy.Value.Result;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                FileOptions.None);

            Span<byte> byteBuffer = stackalloc byte[4096];
            Span<char> charBuffer = stackalloc char[4096];
            var charsWritten = ReadStrmLineZeroAlloc(handle, byteBuffer, charBuffer);
            if (charsWritten <= 0)
            {
                return null;
            }

            var target = new string(charBuffer.Slice(0, charsWritten));
            if (TryGetResolvedUrl(target, out var cached) && !string.IsNullOrEmpty(cached))
            {
                return cached;
            }

            return target;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ReadStrmUrlCoreAsync(string path, CancellationToken ct)
    {
        Interlocked.Increment(ref FileReadExecutionCount);

        // Fast-path fail if the file is missing and the parent folder is readable (no mount drop)
        if (!File.Exists(path) && IsParentDirectoryReadable(path))
        {
            throw new FileNotFoundException($"Could not find file '{path}'.", path);
        }

        SafeFileHandle? handle = null;
        for (var attempt = 0; attempt <= HoldOpenRetryDelays.Length; attempt++)
        {
            try
            {
                handle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    FileOptions.None);
                break;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException && attempt < HoldOpenRetryDelays.Length)
            {
                if (ex is FileNotFoundException && IsParentDirectoryReadable(path))
                {
                    throw;
                }

                await Task.Delay(HoldOpenRetryDelays[attempt], ct).ConfigureAwait(false);
            }
        }

        if (handle is null)
        {
            return null;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        int read;
        try
        {
            using (handle)
            {
                read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, 4096), 0, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        if (read <= 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return null;
        }

        string? target = null;
        try
        {
            Span<char> charBuffer = stackalloc char[4096];
            if (TryExtractFirstStrmLine(buffer.AsSpan(0, read), charBuffer, out var charsWritten))
            {
                target = new string(charBuffer.Slice(0, charsWritten));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (string.IsNullOrEmpty(target))
        {
            return null;
        }

        // If target local path does not exist yet: hold-open retry loop
        if (Path.IsPathRooted(target) && !target.Contains("://", StringComparison.Ordinal))
        {
            if (File.Exists(target))
            {
                return target;
            }

            foreach (var delay in HoldOpenRetryDelays)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                if (File.Exists(target))
                {
                    return target;
                }
            }

            return target;
        }

        if (TryGetResolvedUrl(target, out var cached))
        {
            return cached;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return await ResolveHttpRedirectWithHoldOpenAsync(target, uri, ct).ConfigureAwait(false);
        }

        return target;
    }

    private const int MaxRedirectHops = 5;

    internal static async Task<string> ResolveHttpRedirectWithHoldOpenAsync(string originalUrl, Uri uri, CancellationToken ct)
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
                "QuickSTRM: .strm file unavailable for \"{Name}\": \"{Path}\" ({Kind}): {Message}",
                item.Name,
                item.Path,
                failureKind,
                ex.Message);
            _failedPersistCache[item.Id] = DateTime.UtcNow.Add(TimeSpan.FromMinutes(15));
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
            _failedPersistCache[item.Id] = DateTime.UtcNow.Add(TimeSpan.FromMinutes(15));
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

            _failedPersistCache[item.Id] = DateTime.UtcNow.Add(TimeSpan.FromMinutes(15));
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
                var kind = (statusCode == HttpStatusCode.NotFound || statusCode == HttpStatusCode.ServiceUnavailable)
                    ? FailureKind.PermanentFailure
                    : FailureKind.TemporaryFailure;
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
    /// Executes auto-heal deletion and list feed insertion for a confirmed dead (404/503) .strm item.
    /// Verifies mount health, deletes the local .strm file, extracts TMDB/TVDB provider IDs,
    /// pushes the item to the native AutoHealService feed, removes the item from the Jellyfin library,
    /// and refreshes the parent folder so the dead item disappears from the UI immediately.
    /// </summary>
    public async Task AutoHealDeadItemAsync(BaseItem item, CancellationToken ct = default)
    {
        if (Plugin.Instance?.Configuration is { EnableAutoDelete: false })
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

            // Extract TMDB/TVDB provider ID and push to the AutoHealService feed
            _autoHealService?.AddFromItem(item);

            // Instruct Jellyfin's ILibraryManager to delete the item and refresh the parent folder
            var parent = item.GetParent();
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });

            if (parent is not null)
            {
                try
                {
                    await parent.RefreshMetadata(new MetadataRefreshOptions(_directoryService), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "QuickSTRM: Parent metadata refresh skipped for {ParentName}", parent.Name);
                }
            }

            if (OnStrmDeleted is not null && deletedPath is not null)
            {
                try
                {
                    await OnStrmDeleted.Invoke(deletedPath).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "QuickSTRM: OnStrmDeleted event handler threw exception");
                }
            }

            if (item.Id != Guid.Empty)
            {
                _itemRetryCounts.TryRemove(item.Id, out _);
            }

            _logger.LogWarning("QuickSTRM: Auto-deleted dead .strm link {Name} ({Path}) and pushed to Auto-Heal feed", item.Name, deletedPath);
            if (_telemetryService is not null)
            {
                await _telemetryService.LogAutoHealAsync(deletedPath ?? item.Name, item.Name).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuickSTRM: Auto-delete failed for {Name}", item.Name);
        }
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
                    _itemRetryCounts.TryRemove(kvp.Key, out _);
                    sweptFailed++;
                }
            }
        }

        if (_inMemoryStreams.Count > 1000)
        {
            _inMemoryStreams.Clear();
        }

        if (_probeResultCache.Count > 1000)
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var kvp in _probeResultCache)
            {
                if (kvp.Value.ProbedAtUtc < cutoff)
                {
                    _probeResultCache.TryRemove(kvp.Key, out _);
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

        _autoHealService?.EvictExpiredEntries();
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

    private sealed class ProbeTargetItem : BaseItem
    {
    }

    public Task<MediaInfo?> ProbeOneInlineAsync(BaseItem item, CancellationToken ct = default)
    {
        var source = new MediaSourceInfo
        {
            Id = item.Id.ToString("N"),
            Path = item.Path,
            Protocol = item.Path?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? MediaProtocol.Http : MediaProtocol.File
        };
        return ProbeOneInlineAsync(source, CancellationToken.None, ct, item);
    }

    public async Task<MediaInfo?> ProbeOneInlineAsync(
        MediaSourceInfo source,
        CancellationToken budgetToken,
        CancellationToken ct,
        BaseItem? item = null)
    {
        var itemId = item?.Id ?? (Guid.TryParse(source.Id, out var parsedId) ? parsedId : Guid.Empty);
        if (itemId != Guid.Empty)
        {
            _vfsPrewarmService?.CancelPrewarm(itemId);

            if (_probeResultCache.TryGetValue(itemId, out var cachedProbe) &&
                MediaSourceInterceptor.HasValidVideoStream(cachedProbe.MediaStreams))
            {
                _logger.LogInformation("QuickSTRM: Returning cached probe result for inline item {Id}", itemId);
                if (!string.IsNullOrEmpty(cachedProbe.Container))
                {
                    source.Container = cachedProbe.Container;
                }
                if (item is not null && !string.IsNullOrEmpty(cachedProbe.Container))
                {
                    item.Container = cachedProbe.Container;
                }
                return new MediaInfo
                {
                    MediaStreams = cachedProbe.MediaStreams.ToArray(),
                    RunTimeTicks = cachedProbe.RunTimeTicks,
                    Bitrate = cachedProbe.Bitrate.HasValue ? (int)Math.Min(cachedProbe.Bitrate.Value, int.MaxValue) : null,
                    Size = cachedProbe.Size,
                    Container = cachedProbe.Container
                };
            }
        }

        if (itemId != Guid.Empty && _inflightProbes.TryGetValue(itemId, out var existingTask))
        {
            _logger.LogInformation("QuickSTRM: Awaiting existing in-flight probe for inline item {Id}", itemId);
            MediaInfo? existingResult = null;
            try
            {
                existingResult = await existingTask.ConfigureAwait(false);
            }
            catch
            {
            }

            if (!string.IsNullOrEmpty(item?.Container))
            {
                source.Container = item.Container;
            }

            return existingResult;
        }

        TaskCompletionSource<MediaInfo?>? inlineTcs = null;
        if (itemId != Guid.Empty)
        {
            inlineTcs = new TaskCompletionSource<MediaInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inflightProbes.TryAdd(itemId, inlineTcs.Task))
            {
                if (_inflightProbes.TryGetValue(itemId, out var racedTask))
                {
                    _logger.LogInformation("QuickSTRM: Awaiting existing in-flight probe for inline item {Id}", itemId);
                    MediaInfo? racedResult = null;
                    try
                    {
                        racedResult = await racedTask.ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    if (!string.IsNullOrEmpty(item?.Container))
                    {
                        source.Container = item.Container;
                    }

                    return racedResult;
                }
            }
        }

        MediaInfo? mediaInfo = null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            item ??= (itemId != Guid.Empty ? _libraryManager.GetItemById(itemId) : null)
                     ?? new ProbeTargetItem { Id = itemId != Guid.Empty ? itemId : Guid.NewGuid(), Path = source.Path };
            if (string.IsNullOrEmpty(item.Path))
            {
                item.Path = source.Path;
            }

            // CRITICAL: Inline playback probe bypasses _probeThrottle and _probeGate completely and uses a fast 8-second timeout
            // Decoupled from caller token to ensure SQLite stream persistence and in-memory caching completes even if client drops socket
            mediaInfo = await ExecuteCoreProbeAsync(item, TimeSpan.FromSeconds(8), timeoutCts.Token, bypassProbeGate: true).ConfigureAwait(false);

            if (mediaInfo is not null && !string.IsNullOrEmpty(item.Container))
            {
                source.Container = item.Container;
            }

            return mediaInfo;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (itemId != Guid.Empty && inlineTcs is not null)
            {
                _inflightProbes.TryRemove(itemId, out _);
                inlineTcs.TrySetResult(mediaInfo);
            }
        }
    }

    /// <summary>
    /// Initiates a non-blocking background library scan and enqueues matching .strm items into the persist channel.
    /// Returns immediately without blocking the HTTP request thread.
    /// </summary>
    public Task QueueLibraryScanAsync(ProbeMode mode)
    {
        var isIncremental = mode == ProbeMode.Incremental || mode == ProbeMode.OnlyUnprobed;
        if (!isIncremental)
        {
            _itemRetryCounts.Clear();
        }

        Interlocked.Exchange(ref _isBatchRunning, 1);
        EnsureConsumerRunning();
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
                if (_lowPriorityChannel.Reader.Count == 0 && !_stagedBatchQueue.IsEmpty)
                {
                    ReplenishFromStagedBatches();
                }
                if (Volatile.Read(ref _activeProbes) == 0 &&
                    _highPriorityChannel.Reader.Count == 0 &&
                    _lowPriorityChannel.Reader.Count == 0 &&
                    _stagedBatchQueue.IsEmpty)
                {
                    Interlocked.Exchange(ref _isBatchRunning, 0);
                }
            }
        });

        return Task.CompletedTask;
    }

    internal static int ScanPageSize { get; set; } = 5000;
    internal static int ScanPacingDelayMs { get; set; } = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsStrmFile(ReadOnlySpan<char> path)
    {
        return path.Length >= 5 &&
            path[^5] == '.' &&
            (path[^4] | 0x20) == 's' &&
            (path[^3] | 0x20) == 't' &&
            (path[^2] | 0x20) == 'r' &&
            (path[^1] | 0x20) == 'm';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsStrmFile(BaseItem? item) => item is not null && IsStrmFile(item.Path);

    public static bool IsStrmFile(string? path)
    {
        return path is not null && IsStrmFile(path.AsSpan());
    }

    /// <summary>
    /// Vectorized/fast single-pass unprobed item identification for massive libraries.
    /// Evaluates dimensions in $O(1)$ without object instantiation.
    /// </summary>
    internal static List<BaseItem> IdentifyUnprobedItems(IReadOnlyList<BaseItem> items, bool isIncremental = true)
    {
        var count = items.Count;
        if (items is BaseItem[] array && array.Length >= 50_000)
        {
            var numPartitions = Math.Max(1, Environment.ProcessorCount);
            var partitionSize = (array.Length + numPartitions - 1) / numPartitions;
            var partialResults = new List<BaseItem>[numPartitions];

            Parallel.For(0, numPartitions, p =>
            {
                var start = p * partitionSize;
                var end = Math.Min(start + partitionSize, array.Length);
                var localList = new List<BaseItem>(Math.Min(end - start, 8192));

                for (var i = start; i < end; i++)
                {
                    var item = array[i];
                    if (isIncremental && item.Width > 0 && item.Height > 0)
                    {
                        continue;
                    }

                    var pStr = item.Path;
                    if (pStr is not null && IsStrmFile(pStr.AsSpan()))
                    {
                        localList.Add(item);
                    }
                }

                partialResults[p] = localList;
            });

            var totalUnprobed = 0;
            for (var p = 0; p < numPartitions; p++)
            {
                totalUnprobed += partialResults[p]?.Count ?? 0;
            }

            var combined = new List<BaseItem>(totalUnprobed);
            for (var p = 0; p < numPartitions; p++)
            {
                var part = partialResults[p];
                if (part != null)
                {
                    combined.AddRange(part);
                }
            }
            return combined;
        }

        var unprobed = new List<BaseItem>(Math.Min(count, 32768));

        for (var i = 0; i < count; i++)
        {
            var item = items[i];
            if (isIncremental && item.Width > 0 && item.Height > 0)
            {
                continue;
            }

            var pStr = item.Path;
            if (pStr is not null && IsStrmFile(pStr.AsSpan()))
            {
                unprobed.Add(item);
            }
        }

        return unprobed;
    }

    internal async Task ScanAndQueueLibraryAsync(ProbeMode mode)
    {
        var isIncremental = mode == ProbeMode.Incremental || mode == ProbeMode.OnlyUnprobed;
        if (!isIncremental)
        {
            _failedPersistCache.Clear();
            ClearResolvedUrlCache();
            _itemRetryCounts.Clear();
        }

        var offset = 0;
        var pageSize = ScanPageSize;
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

            var unprobedBatch = (_mediaStreamRepository is not null && isIncremental)
                ? batch.Where(i => IsStrmFile(i.Path) && !HasStreamInfo(i)).ToList()
                : IdentifyUnprobedItems(batch, isIncremental);
            var batchEnqueued = 0;
            for (var i = 0; i < unprobedBatch.Count; i++)
            {
                var item = unprobedBatch[i];
                if (isIncremental && _mediaStreamRepository is null)
                {
                    if (item.Width > 0 && item.Height > 0)
                    {
                        if (_inMemoryStreams.TryGetValue(item.Id, out var cachedStreams) &&
                            !cachedStreams.Any(s => s.Type == MediaStreamType.Audio))
                        {
                            // Missing audio stream in cached streams, do not skip!
                        }
                        else
                        {
                            continue;
                        }
                    }

                    if (_mediaSourceManager != null)
                    {
                        var streams = _mediaSourceManager.GetMediaStreams(item.Id);
                        if (streams != null && streams.Count > 0 && streams.Any(s => s.Type == MediaStreamType.Audio))
                        {
                            continue;
                        }
                    }
                }

                if (EnqueueBackgroundPersist(item, highPriority: false, force: !isIncremental))
                {
                    enqueuedCount++;
                    batchEnqueued++;
                }
            }

            if (batchEnqueued > 0)
            {
                EnsureConsumerRunning();
            }

            offset += batch.Count;
            if (batch.Count < pageSize)
            {
                break;
            }

            if (ScanPacingDelayMs > 0)
            {
                try
                {
                    await Task.Delay(ScanPacingDelayMs, _shutdownCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        var stagedCount = _stagedItemIds.Count;
        var activeQueued = Math.Max(0, enqueuedCount - stagedCount);
        _logger.LogInformation(
            "QuickSTRM: Background library scan completed for {Mode} probe; enqueued {ActiveCount} items into active batch and staged {StagedCount} additional items ({TotalCount} total)",
            mode,
            activeQueued,
            stagedCount,
            enqueuedCount);
    }

    /// <summary>
    /// Starts a probe task. Returns false if one is already running.
    /// </summary>
    public bool StartProbe(ProbeMode mode)
        => StartProbe(mode.ToString());

    /// <summary>
    /// Starts a probe task. Returns false if one is already running.
    /// </summary>
    /// <param name="mode">"Incremental", "Full", "FullRefresh", or "All".</param>
    public bool StartProbe(string mode)
    {
        lock (_lock)
        {
            if (State == "Running")
            {
                return false;
            }

            var isIncremental = string.Equals(mode, "Incremental", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(mode, "OnlyUnprobed", StringComparison.OrdinalIgnoreCase);

            if (!isIncremental)
            {
                _failedPersistCache.Clear();
                ClearResolvedUrlCache();
                _itemRetryCounts.Clear();
            }

            var items = GetStrmItems();
            if (isIncremental)
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
        while (_stagedBatchQueue.TryDequeue(out _))
        {
        }
        _stagedItemIds.Clear();
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

    internal async Task<MediaInfo?> ExecuteCoreProbeAsync(
        BaseItem item,
        TimeSpan ffprobeTimeout,
        CancellationToken ct,
        bool bypassProbeGate = false)
    {
        if (item.Id != Guid.Empty)
        {
            _vfsPrewarmService?.CancelPrewarm(item.Id);
        }

        string? targetUrl;
        if (IsStrmFile(item.Path))
        {
            try
            {
                targetUrl = !ReferenceEquals(_preflightClient, Plugin.HttpClient)
                    ? (ReadStrmTargetFast(item.Path) ?? await ReadStrmUrlAsync(item.Path, ct).ConfigureAwait(false))
                    : await ReadStrmUrlAsync(item.Path, ct).ConfigureAwait(false);
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
                    if (item is not null) { return await HandleProbeStrikeFailureAsync(item, ex, ct, FailureKind.TemporaryFailure).ConfigureAwait(false); }
                    return null;
                }

                _logger.LogWarning(
                    "QuickSTRM: .strm file unavailable for \"{Name}\": \"{Path}\" ({Kind})",
                    item.Name,
                    item.Path,
                    failureKind);
                return await HandleProbeStrikeFailureAsync(item, ex, ct, failureKind).ConfigureAwait(false);
            }
        }
        else
        {
            targetUrl = item.Path;
        }

        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return await HandleProbeStrikeFailureAsync(item, new FileNotFoundException($"Target URL in .strm file is empty for {item.Name}"), ct).ConfigureAwait(false);
        }

        var isHttp = targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        var protocol = isHttp ? MediaProtocol.Http : MediaProtocol.File;

        try
        {
            if (isHttp && !(bypassProbeGate && TryGetResolvedUrl(targetUrl, out _)))
            {
                using var preflightCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var linkedPreflight = CancellationTokenSource.CreateLinkedTokenSource(ct, preflightCts.Token);

                await _headGate.WaitAsync(linkedPreflight.Token).ConfigureAwait(false);
                HttpPreflightResult httpResult;
                try
                {
                    if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
                    {
                        throw new HttpRequestException($"Invalid absolute URL format: {targetUrl}");
                    }

                    httpResult = await CheckHttpEndpointWithHoldOpenRetryAsync(uri, linkedPreflight.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Pre-flight check timed out after 8s for {item.Name}");
                }
                finally
                {
                    ReleaseQuietly(_headGate);
                }

                if (!httpResult.IsHealthy)
                {
                    throw new HttpRequestException(
                        $"Pre-flight check failed after hold-open retries for {item.Name}; status {httpResult.StatusCode} ({httpResult.FailureKind})",
                        null,
                        httpResult.StatusCode);
                }
            }
            else if (!isHttp && (IsStrmFile(item.Path) || (Path.IsPathRooted(targetUrl) && !targetUrl.Contains("://", StringComparison.Ordinal))))
            {
                if (!File.Exists(targetUrl))
                {
                    var localExists = false;
                    foreach (var delay in HoldOpenRetryDelays)
                    {
                        try
                        {
                            await Task.Delay(delay, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            return null;
                        }

                        if (File.Exists(targetUrl))
                        {
                            localExists = true;
                            break;
                        }
                    }

                    if (!localExists)
                    {
                        throw new FileNotFoundException($"Local target missing after hold-open retry window for {item.Name}: {targetUrl}");
                    }
                }
            }

            if (!bypassProbeGate)
            {
                try
                {
                    await _probeGate.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return null;
                }
            }

            try
            {
                using var probeCts = new CancellationTokenSource(ffprobeTimeout);
                using var linkedProbe = CancellationTokenSource.CreateLinkedTokenSource(ct, probeCts.Token);

                var mediaSource = new MediaSourceInfo
                {
                    Id = item.Id.ToString("N"),
                    Path = targetUrl,
                    Protocol = protocol,
                    Type = MediaSourceType.Default,
                    Container = MediaSourceInterceptor.NormalizeContainer(item.Container, targetUrl)
                };

                MediaInfo? mediaInfo;
                try
                {
                    mediaInfo = await Task.Run(() => _mediaEncoder.GetMediaInfo(
                        new MediaInfoRequest
                        {
                            MediaSource = mediaSource,
                            MediaType = DlnaProfileType.Video,
                            ExtractChapters = false
                        },
                        linkedProbe.Token), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Hardened FFprobe killswitch triggered after {ffprobeTimeout.TotalSeconds}s for {item.Name}");
                }

                if (mediaInfo is null)
                {
                    throw new FfmpegException($"FFprobe returned null media info for {item.Name}");
                }

                var videoStream = mediaInfo.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                if (videoStream is not null)
                {
                    if (videoStream.Width.HasValue && videoStream.Width.Value > 0)
                    {
                        item.Width = videoStream.Width.Value;
                    }

                    if (videoStream.Height.HasValue && videoStream.Height.Value > 0)
                    {
                        item.Height = videoStream.Height.Value;
                    }
                }

                if (mediaInfo.RunTimeTicks.HasValue && mediaInfo.RunTimeTicks.Value > 0)
                {
                    item.RunTimeTicks = mediaInfo.RunTimeTicks.Value;
                }

                if (mediaInfo.Size.HasValue && mediaInfo.Size.Value > 0)
                {
                    item.Size = mediaInfo.Size.Value;
                }

                if (!string.IsNullOrEmpty(mediaInfo.Container))
                {
                    var normalized = MediaSourceInterceptor.NormalizeContainer(mediaInfo.Container, targetUrl ?? item.Path);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        item.Container = normalized;
                    }
                }
                else if (!string.IsNullOrEmpty(item.Container))
                {
                    item.Container = MediaSourceInterceptor.NormalizeContainer(item.Container, targetUrl ?? item.Path);
                }

                var streams = mediaInfo.MediaStreams?.ToList() ?? new List<MediaStream>();
                for (var i = 0; i < streams.Count; i++)
                {
                    MediaSourceInterceptor.EnrichMediaStream(streams[i], i, item.Id);
                }

                if (HasStreamInfo(item))
                {
                    ApplyTrickplayPrevention(item);
                }

                if (item.Id != Guid.Empty)
                {
                    var totalBitrate = mediaInfo.Bitrate;
                    if (!totalBitrate.HasValue || totalBitrate.Value <= 0)
                    {
                        var sum = streams.Where(s => s.BitRate.HasValue && s.BitRate.Value > 0).Sum(s => s.BitRate!.Value);
                        totalBitrate = sum > 0 ? sum : 15_000_000;
                        mediaInfo.Bitrate = (int)Math.Min(totalBitrate.Value, int.MaxValue);
                    }

                    foreach (var s in streams)
                    {
                        if (s.Type == MediaStreamType.Video && (!s.BitRate.HasValue || s.BitRate.Value <= 0))
                        {
                            s.BitRate = totalBitrate ?? 15_000_000;
                        }
                    }

                    var cached = new CachedProbeResult
                    {
                        MediaStreams = streams,
                        RunTimeTicks = mediaInfo.RunTimeTicks ?? item.RunTimeTicks,
                        Bitrate = totalBitrate,
                        Size = mediaInfo.Size ?? item.Size,
                        Container = item.Container,
                        ProbedAtUtc = DateTime.UtcNow
                    };
                    _probeResultCache[item.Id] = cached;
                    _inMemoryStreams[item.Id] = streams;

                    if (_mediaStreamRepository is not null)
                    {
                        try
                        {
                            _mediaStreamRepository.SaveMediaStreams(item.Id, streams, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "QuickSTRM: _mediaStreamRepository.SaveMediaStreams failed for {Name}", item.Name);
                        }
                    }

                    await _itemRepository.SaveMediaStreamsAsync(item.Id, streams, CancellationToken.None).ConfigureAwait(false);

                    try
                    {
                        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "QuickSTRM: item.UpdateToRepositoryAsync failed for {Name}; falling back to UpdateItemAsync", item.Name);
                        await _itemRepository.UpdateItemAsync(item, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                RecordSuccess();
                _telemetryService?.IncrementProbed();

                // SUCCESS PATH: Reset retry count on successful probe & stream persistence
                if (item.Id != Guid.Empty)
                {
                    _itemRetryCounts.TryRemove(item.Id, out _);
                }

                return mediaInfo;
            }
            finally
            {
                if (!bypassProbeGate)
                {
                    ReleaseQuietly(_probeGate);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is TimeoutException or HttpRequestException or FfmpegException or FileNotFoundException ||
                                   ex.GetType().Name.Contains("FfmpegException", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleProbeStrikeFailureAsync(item, ex, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Probe execution unexpected error for {Name}", item.Name);
            return await HandleProbeStrikeFailureAsync(item, new FfmpegException(ex.Message, ex), ct).ConfigureAwait(false);
        }
    }

    private async Task<MediaInfo?> HandleProbeStrikeFailureAsync(BaseItem item, Exception ex, CancellationToken ct, FailureKind kind = FailureKind.TemporaryFailure) { try
    {
        // For confirmed permanent failures (e.g. .strm file permanently missing from readable mount),
        // if auto-delete is not disabled, proceed straight to auto-heal without requiring 3 strikes over 45 minutes
        if (kind == FailureKind.PermanentFailure && Plugin.Instance?.Configuration is not { EnableAutoDelete: false })
        {
            _logger.LogWarning(
                "QuickSTRM: Confirmed permanent missing .strm file for \"{Name}\" ({Id}). Auto-healing and cleaning library entry",
                item.Name,
                item.Id);

            _itemRetryCounts.TryRemove(item.Id, out _);
            RecordFailure();
            await AutoHealDeadItemAsync(item, ct).ConfigureAwait(false);
            return null;
        }

        var retryCount = _itemRetryCounts.AddOrUpdate(item.Id, 1, static (_, current) => current + 1);

        if (retryCount < 3)
        {
            _logger.LogWarning(
                "QuickSTRM: Probe failure strike {Count}/3 for \"{Name}\" ({Id}): {Message}. Added to failure cache (15m expiration)",
                retryCount,
                item.Name,
                item.Id,
                ex.Message);

            _failedPersistCache[item.Id] = DateTime.UtcNow.Add(TimeSpan.FromMinutes(15));
            RecordFailure();
            return null;
        }

        _logger.LogWarning(
            "QuickSTRM: Probe failure strike {Count}/3 reached for \"{Name}\" ({Id}): {Message}. Treating as permanent dead link and auto-healing",
            retryCount,
            item.Name,
            item.Id,
            ex.Message);

        _itemRetryCounts.TryRemove(item.Id, out _);
        RecordFailure();
        await AutoHealDeadItemAsync(item, ct).ConfigureAwait(false); } catch (Exception innerEx) { _logger?.LogWarning(innerEx, "QuickSTRM: Error in HandleProbeStrikeFailureAsync for {Name}", item?.Name); }
        return null;
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
                var existingResult = await existingTask.ConfigureAwait(false);
                return existingResult is not null && HasStreamInfo(item);
            }
            catch
            {
                return false;
            }
        }

        TaskCompletionSource<MediaInfo?>? probeTcs = null;
        if (item.Id != Guid.Empty)
        {
            probeTcs = new TaskCompletionSource<MediaInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inflightProbes.TryAdd(item.Id, probeTcs.Task))
            {
                if (_inflightProbes.TryGetValue(item.Id, out var racedTask))
                {
                    try
                    {
                        var racedResult = await racedTask.ConfigureAwait(false);
                        return racedResult is not null && HasStreamInfo(item);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        MediaInfo? mediaInfo = null;
        var probeOk = false;
        try
        {
            onSlotAcquired?.Invoke();
            mediaInfo = await ExecuteCoreProbeAsync(item, timeout, ct).ConfigureAwait(false);
            probeOk = mediaInfo is not null && HasStreamInfo(item);
            trial.Success = probeOk;
            return probeOk;
        }
        finally
        {
            onSlotReleased?.Invoke();
            if (item.Id != Guid.Empty && probeTcs is not null)
            {
                _inflightProbes.TryRemove(item.Id, out _);
                probeTcs.TrySetResult(mediaInfo);
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
        => IsStrmFile(item.Path);

    private static bool HasDimensions(BaseItem item)
        => item.Width > 0 && item.Height > 0;

    public bool HasStreamInfo(BaseItem item)
    {
        if (item.Width <= 0 || item.Height <= 0)
        {
            return false;
        }

        if (_mediaStreamRepository is null || item.Id == Guid.Empty)
        {
            return true;
        }

        if (_probeResultCache.ContainsKey(item.Id) || _inMemoryStreams.ContainsKey(item.Id))
        {
            return true;
        }

        try
        {
            var streams = _mediaStreamRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id });
            if (streams is { Count: > 0 } && streams.Any(s => s.Type == MediaStreamType.Video))
            {
                _inMemoryStreams[item.Id] = streams;
                return true;
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

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

        _libraryManager.ItemAdded -= OnItemAddedOrUpdated;
        _libraryManager.ItemUpdated -= OnItemAddedOrUpdated;

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
        while (_stagedBatchQueue.TryDequeue(out _))
        {
        }
        _stagedItemIds.Clear();
        _resolvedUrlCache.Clear();

        // 3) dispose what we own
        if (!ReferenceEquals(_preflightClient, Plugin.HttpClient))
        {
            if (ReferenceEquals(_redirectResolutionClient, _preflightClient))
            {
                _redirectResolutionClient = Plugin.HttpClient;
            }
            _preflightClient.Dispose();
        }
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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MethodInfo?> CachedSaveMediaStreamsMethods = new();

    /// <summary>
    /// Persists item metadata changes to the repository or database asynchronously.
    /// </summary>
    public static async Task UpdateItemAsync(this IItemRepository? repository, BaseItem item, CancellationToken cancellationToken = default)
    {
        if (repository is not null)
        {
            var repoType = repository.GetType();
            var updateMethod = CachedUpdateMethods.GetOrAdd(repoType, t =>
                t.GetMethod("UpdateItem", new[] { typeof(BaseItem) })
                ?? t.GetMethod("UpdateItem", BindingFlags.Public | BindingFlags.Instance));

            if (updateMethod is not null)
            {
                var res = updateMethod.Invoke(repository, new object[] { item });
                if (res is Task task)
                {
                    await task.ConfigureAwait(false);
                }
                return;
            }
        }

        try
        {
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Synchronous compatibility shim for UpdateItemAsync.
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
                var res = updateMethod.Invoke(repository, new object[] { item });
                if (res is Task task)
                {
                    task.ConfigureAwait(false).GetAwaiter().GetResult();
                }
                return;
            }
        }

        try
        {
            item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Asynchronously persists media stream descriptors directly to Jellyfin's SQLite database via IItemRepository.SaveMediaStreams.
    /// </summary>
    public static async Task SaveMediaStreamsAsync(this IItemRepository? repository, Guid itemId, IReadOnlyList<MediaStream> streams, CancellationToken cancellationToken = default)
    {
        if (repository is null)
        {
            return;
        }

        var repoType = repository.GetType();
        var saveMethod = CachedSaveMediaStreamsMethods.GetOrAdd(repoType, t =>
            t.GetMethod("SaveMediaStreams", BindingFlags.Public | BindingFlags.Instance));

        if (saveMethod is not null)
        {
            var parameters = saveMethod.GetParameters();
            object?[] args;
            if (parameters.Length == 3)
            {
                args = new object?[] { itemId, streams, cancellationToken };
            }
            else if (parameters.Length == 2)
            {
                args = new object?[] { itemId, streams };
            }
            else
            {
                return;
            }

            try
            {
                var result = saveMethod.Invoke(repository, args);
                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Persists media stream descriptors directly to Jellyfin's SQLite database via IItemRepository.SaveMediaStreams.
    /// </summary>
    public static void SaveMediaStreams(this IItemRepository? repository, Guid itemId, IReadOnlyList<MediaStream> streams, CancellationToken cancellationToken = default)
    {
        if (repository is null)
        {
            return;
        }

        var repoType = repository.GetType();
        var saveMethod = CachedSaveMediaStreamsMethods.GetOrAdd(repoType, t =>
            t.GetMethod("SaveMediaStreams", BindingFlags.Public | BindingFlags.Instance));

        if (saveMethod is not null)
        {
            var parameters = saveMethod.GetParameters();
            object?[] args;
            if (parameters.Length == 3)
            {
                args = new object?[] { itemId, streams, cancellationToken };
            }
            else if (parameters.Length == 2)
            {
                args = new object?[] { itemId, streams };
            }
            else
            {
                return;
            }

            try
            {
                var result = saveMethod.Invoke(repository, args);
                if (result is Task task)
                {
                    task.ConfigureAwait(false).GetAwaiter().GetResult();
                }
            }
            catch
            {
            }
        }
    }
}
