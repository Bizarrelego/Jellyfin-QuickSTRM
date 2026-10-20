using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("QuickSTRM: Starting Proactive STRM Dead Link Sweeper task");
        progress?.Report(0);

        var strmItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
            Recursive = true
        }).Where(item => item.Path != null && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)).ToList();

        var total = strmItems.Count;
        if (total == 0)
        {
            _logger.LogInformation("QuickSTRM: No .strm items found in library to sweep");
            progress?.Report(100);
            return;
        }

        _logger.LogInformation("QuickSTRM: Found {Count} .strm items to verify", total);

        var httpClient = _httpClientFactory.CreateClient("QuickSTRM");
        var processed = 0;
        var deadCount = 0;

        foreach (var item in strmItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Paced loop: wait 1000ms between each check to prevent rate-limiting and Core i3 thread exhaustion
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

            try
            {
                if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
                {
                    continue;
                }

                var url = await ProbeService.ReadStrmUrlAsync(item.Path, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }

                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                request.Headers.UserAgent.ParseAdd("QuickSTRM/2.0");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    deadCount++;
                    _logger.LogWarning("QuickSTRM Sweeper: Detected 404 Not Found for {Name} ({Url})", item.Name, url);
                    await _probeService.AutoHealDeadItemAsync(item, cancellationToken).ConfigureAwait(false);
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
                var pct = (double)processed / total * 100.0;
                progress?.Report(Math.Min(100.0, pct));
            }
        }

        progress?.Report(100);
        _logger.LogInformation("QuickSTRM: Proactive sweeper completed. Verified {Total} items, handled {Dead} dead 404 links", total, deadCount);
    }
}
