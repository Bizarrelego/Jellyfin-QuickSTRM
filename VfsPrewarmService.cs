using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace QuickSTRM;

/// <summary>
/// Pre-warms remote HTTP streams and local/VFS mounts (e.g., rclone VFS) by reading the first 5MB
/// of media content predictively when a client views an item's detail page.
/// Caches recently pre-warmed item IDs with a 5-minute sliding expiration to avoid duplicate reads.
/// </summary>
public class VfsPrewarmService : IDisposable
{
    private const int PrewarmSizeBytes = 5 * 1024 * 1024; // 5 MB

    private readonly ILibraryManager _libraryManager;
    private readonly IMemoryCache _cache;
    private readonly bool _ownsCache;
    private readonly ILogger<VfsPrewarmService> _logger;
    private readonly HttpClient _httpClient;

    public VfsPrewarmService(
        ILibraryManager libraryManager,
        ILogger<VfsPrewarmService> logger,
        IMemoryCache? memoryCache = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        _libraryManager = libraryManager;
        _logger = logger;

        if (memoryCache != null)
        {
            _cache = memoryCache;
            _ownsCache = false;
        }
        else
        {
            _cache = new MemoryCache(new MemoryCacheOptions());
            _ownsCache = true;
        }

        _httpClient = httpClientFactory?.CreateClient("QuickSTRM") ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    /// <summary>
    /// Predictively pre-warms the media stream for the specified item ID by reading the first 5MB.
    /// Aborts if the item was already pre-warmed within the last 5 minutes.
    /// </summary>
    public async Task PreWarmItemAsync(string itemId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        var cacheKey = $"QuickSTRM:PreWarm:{itemId}";
        if (_cache.TryGetValue(cacheKey, out _))
        {
            _logger.LogDebug("QuickSTRM: Item {ItemId} was recently pre-warmed within the last 5 minutes; skipping", itemId);
            return;
        }

        // Set sliding expiration of 5 minutes to prevent overlapping reads from clients like Fire TV
        _cache.Set(cacheKey, true, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(5)
        });

        if (!Guid.TryParse(itemId, out var id))
        {
            return;
        }

        var item = _libraryManager.GetItemById(id);
        if (item is null)
        {
            return;
        }

        // If the client opened a Series or Season detail page, locate the first episode to prewarm
        if (item is Series or Season)
        {
            var nextEpisode = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = item.Id,
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true,
                OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
                Limit = 1
            }).FirstOrDefault();

            if (nextEpisode is not null)
            {
                item = nextEpisode;
            }
        }

        if (string.IsNullOrEmpty(item.Path) || !item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!File.Exists(item.Path))
        {
            return;
        }

        var target = await ProbeService.ReadStrmUrlAsync(item.Path, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                await PreWarmHttpRangeAsync(target, item.Name, itemId, timeoutCts.Token).ConfigureAwait(false);
            }
            else
            {
                await PreWarmFileStreamAsync(target, item.Path, item.Name, itemId, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("QuickSTRM: Pre-warming timed out or canceled for {Name} ({ItemId})", item.Name, itemId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Failed to pre-warm stream for {Name} ({ItemId})", item.Name, itemId);
        }
    }

    private async Task PreWarmHttpRangeAsync(string url, string itemName, string itemId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, PrewarmSizeBytes - 1);
        request.Headers.UserAgent.ParseAdd("QuickSTRM/2.0");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("QuickSTRM: HTTP pre-warm for {Name} returned status {Status}", itemName, response.StatusCode);
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long totalRead = 0;
            int read;
            while (totalRead < PrewarmSizeBytes &&
                   (read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, (int)(PrewarmSizeBytes - totalRead))), ct).ConfigureAwait(false)) > 0)
            {
                totalRead += read;
            }

            _logger.LogInformation(
                "QuickSTRM: Pre-warmed {Bytes:N0} bytes via HTTP Range for {Name} ({ItemId})",
                totalRead, itemName, itemId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task PreWarmFileStreamAsync(string target, string strmPath, string itemName, string itemId, CancellationToken ct)
    {
        var localPath = target;
        if (!Path.IsPathRooted(localPath))
        {
            var strmDir = Path.GetDirectoryName(strmPath);
            if (!string.IsNullOrEmpty(strmDir))
            {
                localPath = Path.Combine(strmDir, target);
            }
        }

        if (!File.Exists(localPath))
        {
            _logger.LogDebug("QuickSTRM: Local target file not found for pre-warm: {Path}", localPath);
            return;
        }

        await using var fs = new FileStream(
            localPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            useAsync: true);

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long totalRead = 0;
            int read;
            while (totalRead < PrewarmSizeBytes &&
                   (read = await fs.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, (int)(PrewarmSizeBytes - totalRead))), ct).ConfigureAwait(false)) > 0)
            {
                totalRead += read;
            }

            _logger.LogInformation(
                "QuickSTRM: Pre-warmed {Bytes:N0} bytes via FileStream for {Name} ({ItemId})",
                totalRead, itemName, itemId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
        if (_ownsCache)
        {
            _cache.Dispose();
        }
    }
}
