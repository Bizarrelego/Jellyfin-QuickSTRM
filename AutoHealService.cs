using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace QuickSTRM;

/// <summary>
/// Media classification for auto-healing list feeds.
/// </summary>
public enum AutoHealMedia
{
    Movie,  // For Radarr (TMDB)
    Series  // For Sonarr (TVDB)
}

/// <summary>
/// Represents an unfulfilled media entry in the self-cleaning auto-heal feed.
/// </summary>
public sealed record AutoHealEntry(
    string Id,
    string Title,
    DateTime DateAdded,
    AutoHealMedia MediaType);

/// <summary>
/// Zero-overhead, self-cleaning native list feed service for Radarr and Sonarr auto-healing.
/// Eliminates heavy *arr REST API polling and webhook synchronization in favor of
/// lightweight, pull-based native Custom List endpoints with event-driven pruning and 7-day TTL eviction.
/// </summary>
public sealed class AutoHealService : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<AutoHealService> _logger;
    private readonly TelemetryService? _telemetryService;
    private readonly ConcurrentDictionary<string, AutoHealEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<Guid> _evictionChannel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _drainCts = new();
    private Task? _drainTask;
    private Timer? _ttlTimer;
    private int _batchDrainExecutionCount;
    private bool _disposed;

    /// <summary>
    /// Default time-to-live for unfulfilled recovery entries (7 days).
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets the debounce delay for batching rapid inotify events (default: 500ms).
    /// </summary>
    public TimeSpan DebounceDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gets the number of batch drain operations executed against the library backend.
    /// </summary>
    public int BatchDrainExecutionCount => Volatile.Read(ref _batchDrainExecutionCount);

    public AutoHealService(
        ILibraryManager libraryManager,
        ILogger<AutoHealService> logger,
        TelemetryService? telemetryService = null)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _telemetryService = telemetryService;
    }

    /// <summary>
    /// Gets the total number of entries currently in the recovery dictionary.
    /// </summary>
    public int TotalCount => _entries.Count;

    /// <summary>
    /// Gets the number of Movie entries destined for Radarr.
    /// </summary>
    public int RadarrCount => _entries.Values.Count(e => e.MediaType == AutoHealMedia.Movie);

    /// <summary>
    /// Gets the number of Series entries destined for Sonarr.
    /// </summary>
    public int SonarrCount => _entries.Values.Count(e => e.MediaType == AutoHealMedia.Series);

    /// <summary>
    /// Gets a snapshot of all active entries in the feed.
    /// </summary>
    public IReadOnlyDictionary<string, AutoHealEntry> Entries => _entries;

    /// <summary>
    /// Returns all active recovery feed entries as a read-only list.
    /// </summary>
    public IReadOnlyCollection<AutoHealEntry> GetAllEntries() => _entries.Values.ToArray();

    // ── IHostedService Lifecycle ──

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnLibraryItemChanged;
        _libraryManager.ItemUpdated += OnLibraryItemChanged;

        _drainTask = Task.Run(() => ProcessEvictionLoopAsync(_drainCts.Token));

        // Schedule periodic TTL maintenance sweep every 1 hour
        _ttlTimer = new Timer(
            _ => EvictExpiredEntries(DefaultTtl),
            null,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1));

        _logger.LogInformation("QuickSTRM AutoHealService started: Native list feeds active with 500ms debouncing and 7-day TTL eviction");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnLibraryItemChanged;
        _libraryManager.ItemUpdated -= OnLibraryItemChanged;
        _ttlTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _drainCts.Cancel();
        _evictionChannel.Writer.TryComplete();

        if (_drainTask is not null)
        {
            try
            {
                await Task.WhenAny(_drainTask, Task.Delay(1000, cancellationToken)).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    // ── Feed Ingestion & Extraction ──

    /// <summary>
    /// Adds or updates an entry in the recovery feed.
    /// </summary>
    public void AddEntry(string providerId, string title, AutoHealMedia mediaType)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        var cleanId = providerId.Trim();
        var cleanTitle = string.IsNullOrWhiteSpace(title) ? "Unknown Title" : title.Trim();
        var entry = new AutoHealEntry(cleanId, cleanTitle, DateTime.UtcNow, mediaType);

        _entries[cleanId] = entry;
        _logger.LogInformation(
            "QuickSTRM: Added {Type} to Auto-Heal Feed: '{Title}' [{Id}] (Total Radarr: {RadarrCount}, Sonarr: {SonarrCount})",
            mediaType,
            cleanTitle,
            cleanId,
            RadarrCount,
            SonarrCount);

        _ = _telemetryService?.LogAutoHealAsync($"AutoHealFeed:{mediaType}", $"Queued '{cleanTitle}' ({cleanId}) for {mediaType} recovery.");
    }

    /// <summary>
    /// Extracts provider IDs from a dead BaseItem and enqueues it into the appropriate recovery feed.
    /// </summary>
    public bool AddFromItem(BaseItem item)
    {
        if (item is null)
        {
            return false;
        }

        // TV Episode / Series handling
        if (item is Episode episode)
        {
            var series = episode.Series ?? (_libraryManager.GetItemById(episode.SeriesId) as Series);
            var tvdbId = episode.GetProviderId(MetadataProvider.Tvdb)
                         ?? series?.GetProviderId(MetadataProvider.Tvdb)
                         ?? ExtractProviderId(item, "Tvdb")
                         ?? (series != null ? ExtractProviderId(series, "Tvdb") : null);

            var title = series?.Name ?? item.Name ?? "Unknown Series";

            if (!string.IsNullOrEmpty(tvdbId))
            {
                AddEntry(tvdbId, title, AutoHealMedia.Series);
                return true;
            }

            var tmdbSeriesId = episode.GetProviderId(MetadataProvider.Tmdb)
                               ?? series?.GetProviderId(MetadataProvider.Tmdb)
                               ?? ExtractProviderId(item, "Tmdb")
                               ?? (series != null ? ExtractProviderId(series, "Tmdb") : null);

            if (!string.IsNullOrEmpty(tmdbSeriesId))
            {
                AddEntry(tmdbSeriesId, title, AutoHealMedia.Series);
                return true;
            }
        }
        else if (item is Series seriesItem)
        {
            var tvdbId = seriesItem.GetProviderId(MetadataProvider.Tvdb)
                         ?? ExtractProviderId(seriesItem, "Tvdb");
            var title = seriesItem.Name ?? "Unknown Series";

            if (!string.IsNullOrEmpty(tvdbId))
            {
                AddEntry(tvdbId, title, AutoHealMedia.Series);
                return true;
            }
        }

        // Movie / General Video handling
        var tmdbId = item.GetProviderId(MetadataProvider.Tmdb)
                     ?? ExtractProviderId(item, "Tmdb");
        var movieTitle = item.Name ?? "Unknown Movie";

        if (!string.IsNullOrEmpty(tmdbId))
        {
            AddEntry(tmdbId, movieTitle, AutoHealMedia.Movie);
            return true;
        }

        // Fallback to TVDB for anime or standalone series entries
        var fallbackTvdb = item.GetProviderId(MetadataProvider.Tvdb)
                           ?? ExtractProviderId(item, "Tvdb");
        if (!string.IsNullOrEmpty(fallbackTvdb))
        {
            AddEntry(fallbackTvdb, movieTitle, AutoHealMedia.Series);
            return true;
        }

        _logger.LogWarning("QuickSTRM: Could not extract TMDB or TVDB ID from dead item '{Name}' for Auto-Heal feed", item.Name);
        return false;
    }

    private static string? ExtractProviderId(BaseItem item, string providerKey)
    {
        if (item.ProviderIds is not null && item.ProviderIds.TryGetValue(providerKey, out var id) && !string.IsNullOrWhiteSpace(id))
        {
            return id.Trim();
        }
        return null;
    }

    // ── Event-Driven Self-Cleaning Pruning ──

    private void OnLibraryItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is null)
        {
            return;
        }

        // Lock-free enqueue of item ID; actual parsing and backend lookups are debounced in the background
        _evictionChannel.Writer.TryWrite(e.Item.Id);
    }

    private async Task ProcessEvictionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await _evictionChannel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                // Debounce latch: wait to coalesce rapid inotify events (e.g. Radarr season-pack imports)
                if (DebounceDelay > TimeSpan.Zero)
                {
                    await Task.Delay(DebounceDelay, ct).ConfigureAwait(false);
                }

                await DrainBatchAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "QuickSTRM: Error processing auto-heal batch eviction");
            }
        }
    }

    /// <summary>
    /// Drains all queued item IDs from the channel and executes a single backend lookup batch pass.
    /// </summary>
    public Task<int> DrainBatchAsync()
    {
        if (_entries.IsEmpty)
        {
            while (_evictionChannel.Reader.TryRead(out _)) { }
            return Task.FromResult(0);
        }

        var capacity = _evictionChannel.Reader.CanCount ? Math.Max(32, _evictionChannel.Reader.Count) : 1024;
        var batch = new HashSet<Guid>(capacity);
        while (_evictionChannel.Reader.TryRead(out var id))
        {
            batch.Add(id);
        }

        if (batch.Count == 0)
        {
            return Task.FromResult(0);
        }

        Interlocked.Increment(ref _batchDrainExecutionCount);

        // Perform exactly ONE batch lookup to ILibraryManager instead of N separate lookups
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ItemIds = batch.ToArray()
        });

        var prunedCount = 0;
        foreach (var item in items)
        {
            prunedCount += PruneItem(item);
        }

        return Task.FromResult(prunedCount);
    }

    /// <summary>
    /// Inspects an added or updated library item's provider IDs and removes any matching entries from the recovery feed.
    /// </summary>
    public int PruneItem(BaseItem item)
    {
        if (_entries.IsEmpty)
        {
            return 0;
        }

        var prunedCount = 0;

        // Check the item's own provider IDs
        if (item.ProviderIds is not null)
        {
            foreach (var (_, val) in item.ProviderIds)
            {
                if (!string.IsNullOrWhiteSpace(val) && _entries.TryRemove(val.Trim(), out var removed))
                {
                    prunedCount++;
                    _logger.LogInformation(
                        "QuickSTRM AutoHeal: Pruned '{Title}' [{Id}] from recovery feed (item fulfilled in library)",
                        removed.Title,
                        removed.Id);
                }
            }
        }

        // If it's an episode, also inspect parent Series provider IDs
        if (item is Episode episode)
        {
            var series = episode.Series ?? (_libraryManager.GetItemById(episode.SeriesId) as Series);
            if (series?.ProviderIds is not null)
            {
                foreach (var (_, val) in series.ProviderIds)
                {
                    if (!string.IsNullOrWhiteSpace(val) && _entries.TryRemove(val.Trim(), out var removed))
                    {
                        prunedCount++;
                        _logger.LogInformation(
                            "QuickSTRM AutoHeal: Pruned Series '{Title}' [{Id}] from recovery feed (episode fulfilled in library)",
                            removed.Title,
                            removed.Id);
                    }
                }
            }
        }

        return prunedCount;
    }

    // ── Feed Output Generators ──

    /// <summary>
    /// Generates the Radarr Custom List JSON payload (Movie TMDB IDs).
    /// </summary>
    public object GetRadarrFeed()
    {
        return _entries.Values
            .Where(e => e.MediaType == AutoHealMedia.Movie)
            .OrderByDescending(e => e.DateAdded)
            .Select(e =>
            {
                var isNumeric = int.TryParse(e.Id, out var numId);
                return new
                {
                    title = e.Title,
                    tmdb_id = isNumeric ? (object)numId : e.Id,
                    tmdb = e.Id,
                    id = isNumeric ? (object)numId : e.Id,
                    date_added = e.DateAdded.ToString("o")
                };
            })
            .ToList();
    }

    /// <summary>
    /// Generates the Sonarr Custom List JSON payload (Series TVDB IDs).
    /// </summary>
    public object GetSonarrFeed()
    {
        return _entries.Values
            .Where(e => e.MediaType == AutoHealMedia.Series)
            .OrderByDescending(e => e.DateAdded)
            .Select(e =>
            {
                var isNumeric = int.TryParse(e.Id, out var numId);
                return new
                {
                    title = e.Title,
                    tvdbId = isNumeric ? (object)numId : e.Id,
                    tvdb_id = isNumeric ? (object)numId : e.Id,
                    id = isNumeric ? (object)numId : e.Id,
                    date_added = e.DateAdded.ToString("o")
                };
            })
            .ToList();
    }

    /// <summary>
    /// Sweeps and evicts entries older than the specified TTL (default: 7 days).
    /// </summary>
    public int EvictExpiredEntries(TimeSpan? ttl = null)
    {
        if (_entries.IsEmpty)
        {
            return 0;
        }

        var maxAge = ttl ?? DefaultTtl;
        var cutoff = DateTime.UtcNow - maxAge;
        var evicted = 0;

        foreach (var (key, entry) in _entries)
        {
            if (entry.DateAdded < cutoff)
            {
                if (_entries.TryRemove(key, out var removed))
                {
                    evicted++;
                    _logger.LogInformation(
                        "QuickSTRM AutoHeal: TTL expired for '{Title}' [{Id}] (added {DateAdded:yyyy-MM-dd}); evicted from recovery feed",
                        removed.Title,
                        removed.Id,
                        removed.DateAdded);
                }
            }
        }

        if (evicted > 0)
        {
            _logger.LogInformation("QuickSTRM AutoHeal: TTL maintenance sweep evicted {Count} stale entries (Remaining: {Remaining})", evicted, _entries.Count);
        }

        return evicted;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _drainCts.Cancel();
        _drainCts.Dispose();
        _ttlTimer?.Dispose();
        _libraryManager.ItemAdded -= OnLibraryItemChanged;
        _libraryManager.ItemUpdated -= OnLibraryItemChanged;
    }
}
