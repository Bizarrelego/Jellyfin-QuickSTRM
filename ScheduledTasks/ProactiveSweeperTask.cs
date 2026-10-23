using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
/// Proactive scheduled maintenance task that periodically verifies all .strm files in the library,
/// tests remote URLs with HTTP HEAD requests, and triggers autonomous 404 auto-healing and *arr replacement grabs.
/// </summary>
public class ProactiveSweeperTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProbeService _probeService;
    private readonly ILogger<ProactiveSweeperTask> _logger;

    public ProactiveSweeperTask(
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ProbeService probeService,
        ILogger<ProactiveSweeperTask> logger)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _probeService = probeService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Proactive STRM Dead Link Sweeper";

    /// <inheritdoc />
    public string Key => "QuickStrmProactiveSweeper";

    /// <inheritdoc />
    public string Description => "Paced daily maintenance sweep verifying remote .strm links; auto-deletes 404 dead links and triggers *arr replacement grabs.";

    /// <inheritdoc />
    public string Category => "QuickSTRM";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks // Daily at 3:00 AM
            }
        };
    }

    internal static int SweepPacingDelayMs { get; set; } = 1000;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("QuickSTRM: Starting Proactive STRM Dead Link Sweeper task");
        progress?.Report(0);

        var processed = 0;
        var deadCount = 0;
        var offset = 0;
        const int pageSize = 500;

        while (!cancellationToken.IsCancellationRequested)
        {
            var query = new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video },
                Recursive = true,
                StartIndex = offset,
                Limit = pageSize
            };

            var batch = _libraryManager.GetItemList(query);
            if (batch.Count == 0)
            {
                break;
            }

            var strmItems = batch
                .Where(item => ProbeService.IsStrmFile(item.Path))
                .ToList();

            foreach (var item in strmItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Paced loop: wait SweepPacingDelayMs between each check to prevent rate-limiting and Core i3 thread exhaustion
                if (SweepPacingDelayMs > 0)
                {
                    await Task.Delay(SweepPacingDelayMs, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
                    {
                        continue;
                    }

                    var mediaInfo = await _probeService.ExecuteCoreProbeAsync(
                        item,
                        TimeSpan.FromSeconds(15),
                        cancellationToken).ConfigureAwait(false);

                    if (mediaInfo is null && !File.Exists(item.Path))
                    {
                        deadCount++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "QuickSTRM Sweeper: Verification check error for {Name}", item.Name);
                }
                finally
                {
                    processed++;
                }
            }

            offset += batch.Count;
            if (batch.Count < pageSize)
            {
                break;
            }
        }

        progress?.Report(100);
        _logger.LogInformation("QuickSTRM: Proactive sweeper completed. Verified {Total} items, handled {Dead} dead 404 links", processed, deadCount);
    }
}
