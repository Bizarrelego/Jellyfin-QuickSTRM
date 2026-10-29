using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace QuickSTRM.ScheduledTasks;

/// <summary>
/// Proactive scheduled maintenance task that queries the library for all .strm Video items
/// with missing or invalid video streams, and probes them in the background with controlled concurrency
/// to prevent client playback timeouts.
/// </summary>
public class StrmProbeTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ProbeService _probeService;
    private readonly ILogger<StrmProbeTask> _logger;

    public StrmProbeTask(
        ILibraryManager libraryManager,
        ProbeService probeService,
        ILogger<StrmProbeTask> logger)
    {
        _libraryManager = libraryManager;
        _probeService = probeService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "QuickSTRM: Background Library Probe";

    /// <inheritdoc />
    public string Key => "QuickSTRMBackgroundProbeTask";

    /// <inheritdoc />
    public string Description => "Scans all .strm files with missing or empty codecs and probes them in the background to prevent client timeouts during playback.";

    /// <inheritdoc />
    public string Category => "QuickSTRM";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Run automatically every night at 3:30 AM
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3.5).Ticks // 3:30 AM
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("QuickSTRM: Starting proactive background library probe task...");
        progress?.Report(0);

        // 1. Fetch all Video items (.strm) that lack valid video streams
        var query = new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
            IsVirtualItem = false,
            Recursive = true
        };

        var allItems = _libraryManager.GetItemList(query);
        var unprobedItems = allItems
            .Where(i => !string.IsNullOrEmpty(i.Path) &&
                        ProbeService.IsStrmFile(i.Path) &&
                        File.Exists(i.Path) &&
                        !HasValidCodecStreams(i))
            .ToList();

        if (unprobedItems.Count == 0)
        {
            _logger.LogInformation("QuickSTRM: No unprobed .strm items found in library.");
            progress?.Report(100.0);
            return;
        }

        _logger.LogInformation(
            "QuickSTRM: Found {Count} unprobed .strm items. Beginning throttled background probe...",
            unprobedItems.Count);

        var completed = 0;
        using var throttle = new SemaphoreSlim(2); // Maximum 2 concurrent stream probes to protect I/O & sockets

        var tasks = unprobedItems.Select(async item =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // 2. Invoke ffprobe via ProbeService
                var mediaInfo = await _probeService.ExecuteCoreProbeAsync(
                    item,
                    TimeSpan.FromSeconds(15),
                    cancellationToken).ConfigureAwait(false);

                if (mediaInfo != null)
                {
                    // 3. Persist metadata directly to Jellyfin database
                    try
                    {
                        await _libraryManager.UpdateItemAsync(
                            item,
                            item.GetParent(),
                            ItemUpdateType.MetadataEdit,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "QuickSTRM: LibraryManager.UpdateItemAsync fallback for \"{Name}\"", item.Name);
                    }

                    _logger.LogInformation(
                        "QuickSTRM: Successfully probed and saved metadata for \"{Name}\" (Container: {Container}, Runtime: {Runtime})",
                        item.Name,
                        item.Container,
                        item.RunTimeTicks.HasValue ? TimeSpan.FromTicks(item.RunTimeTicks.Value).ToString(@"hh\:mm\:ss") : "unknown");
                }
                else
                {
                    _logger.LogWarning("QuickSTRM: Background probe produced no stream info for \"{Name}\"", item.Name);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("QuickSTRM: Failed background probe for \"{Name}\": {Message}", item.Name, ex.Message);
            }
            finally
            {
                throttle.Release();
                var done = Interlocked.Increment(ref completed);
                progress?.Report((double)done / unprobedItems.Count * 100.0);
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        progress?.Report(100.0);
        _logger.LogInformation("QuickSTRM: Background library probe task completed. Processed {Count} items.", completed);
    }

    private bool HasValidCodecStreams(BaseItem item)
    {
        // Check in-memory probe cache first
        if (item.Id != Guid.Empty &&
            _probeService.TryGetCachedProbeResult(item.Id, out var cached) &&
            cached?.MediaStreams != null &&
            MediaSourceInterceptor.HasValidVideoStream(cached.MediaStreams))
        {
            return true;
        }

        // Check item's persisted media streams
        var streams = item.GetMediaStreams();
        return MediaSourceInterceptor.HasValidVideoStream(streams);
    }
}
