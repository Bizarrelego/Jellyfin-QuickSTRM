using System;
using System.Collections.Generic;
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

namespace QuickSTRM;

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

    // protects memory: one entry per queued/running item ID (not per request), so N repeated play attempts of the same item
    // occupy exactly one slot instead of N parked tasks, each of which would otherwise pin a BaseItem and a closure in RAM
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _pendingPersist = new();

    // number of queued items that already hold a probe slot (running); waiting = pending - running
    private int _runningPersist;

    // protects memory on a low-end host: hard bound on queued items, so even a flood of DISTINCT items can't grow without limit
    private const int MaxPendingPersist = 500;

    // negative cache of items whose background probe failed or timed out; dead URLs are rejected here until the entry expires
    private readonly IMemoryCache _failedPersistCache;

    // tracks keys stored in _failedPersistCache so ClearFailureCache can flush them without clearing unrelated Jellyfin cache items
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _failedItemIds = new();

    // absolute expiration: entry expires exactly 1 hour after failure regardless of how often it is queried;
    // prevents perpetual lockouts from automated client retries continuously resetting a sliding window
    private static readonly TimeSpan FailedPersistAbsoluteExpiration = TimeSpan.FromHours(1);

    private static string FailureKey(Guid itemId) => "QuickSTRM:persist-failed:" + itemId.ToString("N");

    /// <summary>Gets the number of items queued for background persistence that are still waiting for a probe slot.</summary>
    public int PersistQueueWaiting => Math.Max(0, _pendingPersist.Count - Volatile.Read(ref _runningPersist));

    /// <summary>Gets the number of queued items currently being persisted (holding a probe slot).</summary>
    public int PersistQueueRunning => Volatile.Read(ref _runningPersist);

    /// <summary>Gets the total number of items queued or running for background persistence.</summary>
    public int PersistQueueTotal => _pendingPersist.Count;

    // protects low-end CPUs (e.g. Core i3): hard ceiling of exactly 2 concurrent ffprobe-backed refreshes across ALL callers
    // (bulk, auto-probe and inline), so probes can never exceed the 2 threads we can spare without starving playback
    private const int MaxConcurrentProbes = 2;
    private readonly SemaphoreSlim _probeGate = new(MaxConcurrentProbes, MaxConcurrentProbes);

    // protects bulk imports: generous 60s lets slow cloud-drive links finish without failing the library scan
    private static readonly TimeSpan BackgroundProbeTimeout = TimeSpan.FromSeconds(60);

    // protects connected clients: inline (playback-time) probes must answer within 5s or the client's own request times out
    // and the user is left with a spinner while a useless ffprobe keeps burning CPU
    private static readonly TimeSpan InlineProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Gets the current task state: Idle, Running, or Stopping.</summary>
    public string State { get; private set; } = "Idle";

    /// <summary>Gets the total number of items in the current probe batch.</summary>
    public int TotalItems { get; private set; }

    /// <summary>Gets the number of items already processed.</summary>
    public int ProcessedItems { get; private set; }

    /// <summary>Gets the name of the item currently being probed.</summary>
    public string CurrentItemName { get; private set; } = string.Empty;

    private readonly IMediaEncoder _mediaEncoder;

    public ProbeService(
        ILibraryManager libraryManager,
        IDirectoryService directoryService,
        IMediaEncoder mediaEncoder,
        IMemoryCache memoryCache,
        ILogger<ProbeService> logger)
    {
        _libraryManager = libraryManager;
        _directoryService = directoryService;
        _mediaEncoder = mediaEncoder;
        _failedPersistCache = memoryCache;
        _logger = logger;
    }

    // ── IHostedService lifecycle ──

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        StopProbe();
        return Task.CompletedTask;
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

        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("QuickSTRM: Auto-probing new item {Name}", item.Name);

                // protects low-end CPUs: the shared 2-slot gate is acquired inside ProbeItemAsync, so a bulk import
                // queues here instead of spawning unbounded ffprobe processes
                await ProbeItemAsync(item, BackgroundProbeTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QuickSTRM: Auto-probe failed for {Name}", item.Name);
            }
        });
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
    /// shared 5-second budget. Does NOT touch the database or run a metadata refresh.
    /// The result is aligned with <paramref name="sources"/>; an entry is null if that source timed out, got no slot or failed.
    /// </summary>
    public async Task<MediaInfo?[]> ProbeSourcesInlineAsync(
        IReadOnlyList<MediaSourceInfo> sources,
        CancellationToken ct)
    {
        // protects low-end CPUs: ONE linked token carries the 5s total cap and the client's cancellation into every encoder call,
        // so N sources can never stretch playback start to N x 5s, and cancellation reaches the process owner (the encoder)
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(InlineProbeTimeout);

        // improves latency: sources are probed concurrently, while the shared 2-slot gate keeps at most 2 ffprobe processes alive
        var tasks = new Task<MediaInfo?>[sources.Count];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = ProbeOneInlineAsync(sources[i], budget.Token, ct);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Fire-and-forget: queues a standard background RefreshMetadata pass so the stream info is stored in the database
    /// and later plays take the zero-probe fast path. Never blocks or fails the caller.
    /// </summary>
    public bool EnqueueBackgroundPersist(BaseItem item)
    {
        // protects memory: already-persisted items never enter the queue
        if (HasStreamInfo(item))
        {
            return false;
        }

        // prevents thread pool starvation on constrained hardware: a known-dead URL is rejected before any Task is created, so it
        // can neither park a pool thread/continuation on the 2-slot gate nor burn a slot for 60s of a doomed probe; absolute expiration
        // prevents perpetual lockouts by ensuring the entry expires after 1 hour even under repeated client retries
        if (_failedPersistCache.TryGetValue(FailureKey(item.Id), out _))
        {
            return false;
        }

        // protects memory: bounded queue; when full we drop the request (the item is simply retried on a later play)
        if (_pendingPersist.Count >= MaxPendingPersist)
        {
            _logger.LogWarning("QuickSTRM: Persist queue full ({Max}); dropping {Name}", MaxPendingPersist, item.Name);
            return false;
        }

        // protects memory: TryAdd is atomic, so concurrent plays of the same item race safely and exactly one wins; the losers
        // return here without allocating a task or closure, which keeps repeated playback attempts from flooding the queue
        if (!_pendingPersist.TryAdd(item.Id, 0))
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // improves efficiency: persisting once removes the need to re-probe this item in memory on every later play;
                // it goes through the shared 2-slot gate, so it can never exceed the process limit
                var ok = await ProbeItemAsync(
                    item,
                    BackgroundProbeTimeout,
                    CancellationToken.None,
                    onSlotAcquired: () => Interlocked.Increment(ref _runningPersist),
                    onSlotReleased: () => Interlocked.Decrement(ref _runningPersist)).ConfigureAwait(false);

                // Jellyfin's refresh can finish "successfully" without stream info when ffprobe fails inside it, so success
                // is judged by the item actually having a runtime afterwards, not only by the absence of an exception
                if (!ok || !HasStreamInfo(item))
                {
                    MarkPersistFailed(item);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "QuickSTRM: Background persist failed for {Name}", item.Name);
                MarkPersistFailed(item);
            }
            finally
            {
                // protects memory: always free the ID (even on failure) so the entry can't leak and block the item forever
                _pendingPersist.TryRemove(item.Id, out _);
            }
        });

        return true;
    }

    /// <summary>
    /// Flushes the negative failure cache, allowing previously failed items to be retried immediately.
    /// </summary>
    public void ClearFailureCache()
    {
        foreach (var id in _failedItemIds.Keys)
        {
            _failedPersistCache.Remove(FailureKey(id));
        }

        _failedItemIds.Clear();
        _logger.LogInformation("QuickSTRM: Flushed negative persistence failure cache");
    }

    private void MarkPersistFailed(BaseItem item)
    {
        // prevents perpetual lockouts from automated client retries: absolute expiration of exactly 1 hour is enforced instead
        // of a sliding window, so background client requests or automated retries cannot continuously postpone the retry opportunity
        _failedItemIds.TryAdd(item.Id, 0);

        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = FailedPersistAbsoluteExpiration
        };

        options.RegisterPostEvictionCallback((key, value, reason, state) =>
        {
            if (state is Guid id)
            {
                _failedItemIds.TryRemove(id, out _);
            }
        }, item.Id);

        _failedPersistCache.Set(
            FailureKey(item.Id),
            0,
            options);

        _logger.LogWarning(
            "QuickSTRM: Background persist failed for {Name}; negative cache set with {Hours}h absolute expiration (prevents perpetual lockout)",
            item.Name,
            FailedPersistAbsoluteExpiration.TotalHours);
    }

    private async Task<MediaInfo?> ProbeOneInlineAsync(
        MediaSourceInfo source,
        CancellationToken budgetToken,
        CancellationToken ct)
    {
        try
        {
            // protects low-end CPUs: shares the same 2-slot gate as background probes; the budget also covers the slot wait
            await _probeGate.WaitAsync(budgetToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            // improves latency: ffprobe only, no provider lookups or DB writes, so the 5s budget is spent on the network probe
            return await _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaSource = source,
                    MediaType = DlnaProfileType.Video,
                    ExtractChapters = false
                },
                budgetToken).ConfigureAwait(false);
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
            _probeGate.Release();
        }
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

    /// <summary>
    /// Triggers the official Jellyfin metadata refresh with remote content probing
    /// for a single item. This writes real probe data into the database.
    /// </summary>
    private async Task<bool> ProbeItemAsync(
        BaseItem item,
        TimeSpan timeout,
        CancellationToken ct,
        Action? onSlotAcquired = null,
        Action? onSlotReleased = null)
    {
        // protects low-end CPUs: only background work uses the heavy RefreshMetadata path; playback never waits on it
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }

        // lets the persistence queue distinguish items waiting for a slot from items actually probing
        onSlotAcquired?.Invoke();

        try
        {
            // protects bulk imports: the 60s clock starts only once we hold a slot, so queue time can't cause false timeouts
            timeoutCts.CancelAfter(timeout);

            // NOTE: this plugin does not launch ffprobe itself. RefreshMetadata -> Jellyfin's IMediaEncoder starts the process
            // and ties it to this token. We cancel the token rather than holding a Process handle we do not own.
            await item.RefreshMetadata(
                new MetadataRefreshOptions(_directoryService)
                {
                    EnableRemoteContentProbe = true,
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh
                },
                timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // a timeout is skipped rather than mistaken for a user stop, so batch probing continues
            _logger.LogWarning("QuickSTRM: Probe timed out after {Seconds}s for {Name}", timeout.TotalSeconds, item.Name);
            return false;
        }
        finally
        {
            // protects low-end CPUs: always release the slot (even on error/cancel) so the 2-slot limit can't leak shut
            onSlotReleased?.Invoke();
            _probeGate.Release();
        }
    }

    private List<BaseItem> GetStrmItems()
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

    public void Dispose()
    {
        _cts?.Dispose();
        _probeGate.Dispose();
    }
}
