using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace QuickSTRM;

/// <summary>
/// Monitors active playback sessions to pre-warm next episodes in the background
/// and defer virtual chapter synthesis until playback completes to avoid SQLite write-lock contention.
/// </summary>
public sealed class SessionMonitorService : IHostedService, IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ProbeService _probeService;
    private readonly IChapterManager? _chapterManager;
    private readonly VfsPrewarmService? _prewarmService;
    private readonly ILogger<SessionMonitorService> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _prewarmedSessions = new();
    private int _disposed;

    public SessionMonitorService(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ProbeService probeService,
        ILogger<SessionMonitorService> logger,
        IChapterManager? chapterManager = null,
        VfsPrewarmService? prewarmService = null)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _probeService = probeService;
        _logger = logger;
        _chapterManager = chapterManager;
        _prewarmService = prewarmService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _prewarmedSessions.Clear();
        return Task.CompletedTask;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item?.Id != null && e.Item.Id != Guid.Empty)
        {
            _prewarmService?.CancelPrewarm(e.Item.Id.ToString("N"));
            _prewarmService?.CancelPrewarm(e.Item.Id.ToString());
        }

        var sessionId = e.Session?.Id;
        if (!string.IsNullOrEmpty(sessionId) && !_prewarmedSessions.TryAdd(sessionId, 0))
        {
            return;
        }

        var config = Plugin.Instance?.Configuration;
        if (config is not { EnableNextEpisodePreWarm: true })
        {
            return;
        }

        if (e.Item is not Episode currentEpisode || !currentEpisode.IndexNumber.HasValue)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                PreWarmNextEpisode(currentEpisode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "QuickSTRM: Failed to pre-warm next episode after playback start on {Name}", currentEpisode.Name);
            }
        });
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        // Check lightweight boolean latch before querying SQLite or allocating tasks
        var sessionId = e.Session?.Id;
        if (string.IsNullOrEmpty(sessionId) || _prewarmedSessions.ContainsKey(sessionId))
        {
            return;
        }

        var config = Plugin.Instance?.Configuration;
        if (config is not { EnableNextEpisodePreWarm: true })
        {
            return;
        }

        if (e.Item is not Episode currentEpisode || !currentEpisode.IndexNumber.HasValue)
        {
            return;
        }

        if (_prewarmedSessions.TryAdd(sessionId, 0))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    PreWarmNextEpisode(currentEpisode);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "QuickSTRM: Failed to pre-warm next episode during playback on {Name}", currentEpisode.Name);
                }
            });
        }
    }

    private void PreWarmNextEpisode(Episode currentEpisode)
    {
        var seriesId = currentEpisode.SeriesId;
        if (seriesId == Guid.Empty)
        {
            return;
        }

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = seriesId,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true
        }).OfType<Episode>().ToList();

        if (episodes.Count == 0)
        {
            return;
        }

        var currentSeason = currentEpisode.ParentIndexNumber ?? 1;
        var nextEpNum = currentEpisode.IndexNumber!.Value + 1;

        // 1. Look for next episode in the same season
        var nextEpisode = episodes.FirstOrDefault(ep =>
            (ep.ParentIndexNumber ?? 1) == currentSeason &&
            ep.IndexNumber == nextEpNum);

        // 2. If not found (e.g. season finale), look for E01 in the following season
        if (nextEpisode is null)
        {
            var nextSeason = currentSeason + 1;
            nextEpisode = episodes
                .Where(ep => (ep.ParentIndexNumber ?? 1) == nextSeason && ep.IndexNumber.HasValue)
                .OrderBy(ep => ep.IndexNumber!.Value)
                .FirstOrDefault();
        }

        if (nextEpisode is null)
        {
            return;
        }

        // Only pre-warm .strm files that lack stream info
        var isStrm = ProbeService.IsStrmFile(nextEpisode.Path);
        var hasStreamInfo = nextEpisode.RunTimeTicks.HasValue && nextEpisode.RunTimeTicks.Value > 0;

        if (isStrm && !hasStreamInfo)
        {
            _logger.LogInformation(
                "QuickSTRM: Next-episode pre-warming triggered for {Series} S{Season:00}E{Episode:00} ({Name})",
                currentEpisode.SeriesName,
                nextEpisode.ParentIndexNumber ?? 1,
                nextEpisode.IndexNumber ?? 1,
                nextEpisode.Name);

            _probeService.EnqueueBackgroundPersist(nextEpisode, highPriority: true);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackProgressEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Session?.Id))
        {
            _prewarmedSessions.TryRemove(e.Session.Id, out _);
        }

        if (_prewarmedSessions.Count > 1000)
        {
            _prewarmedSessions.Clear();
        }

        var config = Plugin.Instance?.Configuration;
        if (config is not { SynthesizeVirtualChapters: true })
        {
            return;
        }

        var item = e.Item;
        if (item is not (Episode or Movie))
        {
            return;
        }

        // Fire-and-forget background execution so playback stopping lifecycle is never blocked
        _ = Task.Run(() =>
        {
            try
            {
                DeferredSynthesizeVirtualChapters(item);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "QuickSTRM: Deferred virtual chapter synthesis failed for {Name}", item.Name);
            }
        });
    }

    private void DeferredSynthesizeVirtualChapters(BaseItem item)
    {
        // 1. Verify item is a .strm file
        if (!ProbeService.IsStrmFile(item.Path))
        {
            return;
        }

        // 2. Handle edge case: Avoid synthesizing chapters for un-probed items with missing or non-positive RunTimeTicks
        // (e.g. user scrubs to the end of a video within the first 5 minutes before probing has completed)
        if (!item.RunTimeTicks.HasValue || item.RunTimeTicks.Value <= 0)
        {
            _logger.LogDebug("QuickSTRM: Skipping virtual chapter synthesis for {Name}; runtime is not yet resolved", item.Name);
            return;
        }

        if (_chapterManager is null)
        {
            return;
        }

        // 3. Verify the item currently has 0 chapters
        var existingChapters = _chapterManager.GetChapters(item.Id);
        if (existingChapters is not null && existingChapters.Count > 0)
        {
            return;
        }

        // 4. Synthesize virtual chapters every 5 minutes
        var fiveMinutesTicks = TimeSpan.FromMinutes(5).Ticks;
        var runtime = item.RunTimeTicks.Value;
        var chapters = new List<ChapterInfo>();
        var index = 1;

        for (long pos = 0; pos < runtime; pos += fiveMinutesTicks)
        {
            chapters.Add(new ChapterInfo
            {
                Name = $"Chapter {index++}",
                StartPositionTicks = pos
            });
        }

        if (chapters.Count > 0)
        {
            if (item is MediaBrowser.Controller.Entities.Video videoItem)
            {
                _chapterManager.SaveChapters(videoItem, chapters);
            }

            _logger.LogInformation(
                "QuickSTRM: Deferred synthesis generated {Count} virtual chapters for {Name} following playback completion",
                chapters.Count,
                item.Name);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _prewarmedSessions.Clear();
    }
}
