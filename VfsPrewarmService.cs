using System;
using System.Buffers;
using System.Collections.Generic;
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
    private const int TailPrewarmSizeBytes = 1024 * 1024; // 1 MB
    private const long TailThresholdBytes = 6L * 1024 * 1024; // 6 MB
    private const int BufferSize = 81920; // 80 KB

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
    /// Checks synchronously whether an item ID was recently pre-warmed within the sliding cache expiration window.
    /// Fast synchronous path avoiding closure and Task.Run allocations.
    /// </summary>
    public bool IsRecentlyWarmed(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        return _cache.TryGetValue($"QuickSTRM:PreWarm:{itemId}", out _);
    }

    /// <summary>
    /// Checks synchronously whether an item ID was recently pre-warmed within the sliding cache expiration window.
    /// </summary>
    public bool IsRecentlyWarmed(Guid itemId)
        => IsRecentlyWarmed(itemId.ToString("N")) || IsRecentlyWarmed(itemId.ToString("D"));

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

        if (IsRecentlyWarmed(itemId))
        {
            _logger.LogDebug("QuickSTRM: Item {ItemId} was recently pre-warmed within the last 5 minutes; skipping", itemId);
            return;
        }

        var cacheKey = $"QuickSTRM:PreWarm:{itemId}";
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

            // Adjacent Subtitle Pre-Warming
            await PreWarmAdjacentSubtitlesAsync(item.Path, timeoutCts.Token).ConfigureAwait(false);
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
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
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

        // Tail Pre-Warming for HTTP (MP4/MOV rear moov atom fix)
        var isMp4OrMov = false;
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri))
        {
            var uriPath = parsedUri.AbsolutePath;
            if (uriPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                uriPath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            {
                isMp4OrMov = true;
            }
        }

        if (!isMp4OrMov && string.Equals(response.Content.Headers.ContentType?.MediaType, "video/mp4", StringComparison.OrdinalIgnoreCase))
        {
            isMp4OrMov = true;
        }

        long totalSize = 0;
        if (response.Content.Headers.ContentRange?.Length.HasValue == true)
        {
            totalSize = response.Content.Headers.ContentRange.Length.Value;
        }
        else if (response.Content.Headers.ContentLength.HasValue)
        {
            totalSize = response.Content.Headers.ContentLength.Value;
        }

        if (isMp4OrMov && totalSize > TailThresholdBytes)
        {
            await PreWarmHttpTailAsync(url, itemName, ct).ConfigureAwait(false);
        }
    }

    private async Task PreWarmHttpTailAsync(string url, string itemName, CancellationToken ct)
    {
        try
        {
            using var tailRequest = new HttpRequestMessage(HttpMethod.Get, url);
            tailRequest.Headers.TryAddWithoutValidation("Range", "bytes=-1048576");
            tailRequest.Headers.UserAgent.ParseAdd("QuickSTRM/2.0");

            using var tailResponse = await _httpClient.SendAsync(tailRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!tailResponse.IsSuccessStatusCode)
            {
                return;
            }

            await using var tailStream = await tailResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long tailRead = 0;
                int read;
                while (tailRead < TailPrewarmSizeBytes &&
                       (read = await tailStream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, (int)(TailPrewarmSizeBytes - tailRead))), ct).ConfigureAwait(false)) > 0)
                {
                    tailRead += read;
                }

                _logger.LogInformation("QuickSTRM: Tail pre-warmed {Bytes:N0} bytes (moov atom) via HTTP Range for {Name}", tailRead, itemName);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "QuickSTRM: Tail pre-warm skipped or failed for {Name}", itemName);
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
            bufferSize: BufferSize,
            useAsync: true);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
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

            // Tail Pre-Warming for local mounted files (MP4/MOV rear moov atom fix)
            var isMp4OrMov = localPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                             localPath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);

            if (isMp4OrMov && fs.Length > TailThresholdBytes)
            {
                fs.Seek(fs.Length - TailPrewarmSizeBytes, SeekOrigin.Begin);
                long tailRead = 0;
                while (tailRead < TailPrewarmSizeBytes &&
                       (read = await fs.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, (int)(TailPrewarmSizeBytes - tailRead))), ct).ConfigureAwait(false)) > 0)
                {
                    tailRead += read;
                }

                _logger.LogInformation("QuickSTRM: Tail pre-warmed {Bytes:N0} bytes (moov atom) via FileStream for {Name} ({ItemId})", tailRead, itemName, itemId);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task PreWarmAdjacentSubtitlesAsync(string strmPath, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(strmPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return;
            }

            var prefix = Path.GetFileNameWithoutExtension(strmPath);
            IEnumerable<string> candidateFiles;
            try
            {
                candidateFiles = Directory.EnumerateFiles(dir, $"{prefix}*");
            }
            catch (Exception)
            {
                return;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                foreach (var file in candidateFiles)
                {
                    var ext = Path.GetExtension(file.AsSpan());
                    if (ext.Equals(".srt", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".ass", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".ssa", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".vtt", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            await using var subFs = new FileStream(
                                file,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite,
                                bufferSize: 64 * 1024,
                                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                            _ = await subFs.ReadAsync(buffer.AsMemory(0, 64 * 1024), ct).ConfigureAwait(false);
                            _logger.LogDebug("QuickSTRM: Pre-warmed adjacent subtitle: {Path}", file);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "QuickSTRM: Adjacent subtitle pre-warming skipped for {Path}", strmPath);
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
