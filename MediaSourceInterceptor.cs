#nullable disable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace QuickSTRM;

/// <summary>
/// Decorator that wraps the original <see cref="IMediaSourceManager"/>.
/// Only <see cref="GetPlaybackMediaSources"/> contains interception logic;
/// all other members delegate 100% to the inner instance.
/// </summary>
public sealed class MediaSourceInterceptor : IMediaSourceManager, IDisposable
{
    private readonly IMediaSourceManager _inner;
    private readonly ILogger<MediaSourceInterceptor> _logger;

    // avoids a DI cycle at startup (ProbeService -> ILibraryManager -> ... -> IMediaSourceManager) and keeps startup cheap on
    // a Core i3: the ProbeService is resolved only on the first first-play request instead of during container construction
    private readonly Func<ProbeService> _probeServiceFactory;

    public MediaSourceInterceptor(
        IMediaSourceManager inner,
        ILogger<MediaSourceInterceptor> logger,
        Func<ProbeService> probeServiceFactory)
    {
        _inner = inner;
        _logger = logger;
        _probeServiceFactory = probeServiceFactory;
    }

    /// <summary>
    /// Core interception point — three-way routing for .strm playback:
    /// 1. Non-.strm → passthrough (allowMediaProbe unchanged)
    /// 2. .strm with RunTimeTicks > 0 → allowMediaProbe = false (skip ffprobe)
    /// 3. .strm without stream info → capped (5s) inline probe, then resolve without a second probe;
    ///    on timeout/queue starvation fall back to the unprobed .strm source
    /// </summary>
    public Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        var isStrm = item.Path != null
            && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

        if (!isStrm)
        {
            // improves performance: non-.strm items return the inner task directly, so no async state machine is allocated
            return _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe, enablePathSubstitution, cancellationToken);
        }

        if (item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0)
        {
            _logger.LogDebug(
                "QuickSTRM: Skipping ffprobe for {Name} (RunTimeTicks={Ticks})",
                item.Name,
                item.RunTimeTicks.Value);

            // improves performance: already-probed .strm items skip a remote ffprobe on every playback start, and the
            // direct task handoff means zero extra allocations on the hot path of a Core i3
            return _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe: false, enablePathSubstitution, cancellationToken);
        }

        if (!allowMediaProbe)
        {
            // protects the Core i3: if the caller already forbade probing we must not start one, and we stay synchronous
            return _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe: false, enablePathSubstitution, cancellationToken);
        }

        // only this rare first-play path pays for an async state machine, because it must await the capped probe
        return ProbeThenResolveAsync(item, user, enablePathSubstitution, cancellationToken);
    }

    private async Task<IReadOnlyList<MediaSourceInfo>> ProbeThenResolveAsync(
        BaseItem item,
        User user,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("QuickSTRM: Inline-probing first-play {Name} (5s total cap, in memory)", item.Name);

        // improves client stability: the stock manager (with probing disabled) still builds the complete list - static plus
        // dynamic sources, per-user permission flags and ordering - so we only fill in stream info and never re-implement that logic
        var resolved = await _inner.GetPlaybackMediaSources(
            item, user, allowMediaProbe: false, enablePathSubstitution, cancellationToken)
            .ConfigureAwait(false);

        var persistNeeded = false;

        try
        {
            // protects the Core i3: only the item's own static sources are probed; live/dynamic sources are never touched
            var staticIds = new HashSet<string>();
            foreach (var staticSource in _inner.GetStaticMediaSources(item, enablePathSubstitution, user))
            {
                staticIds.Add(staticSource.Id);
            }

            var targets = new List<MediaSourceInfo>();
            foreach (var source in resolved)
            {
                if (source.Type != MediaSourceType.Placeholder
                    && staticIds.Contains(source.Id)
                    && !HasVideoStream(source))
                {
                    targets.Add(source);
                }
            }

            if (targets.Count == 0)
            {
                return resolved;
            }

            // improves efficiency: from here on the item is unprobed in the database, so it must be persisted whether the inline
            // probe succeeds, times out or fails; otherwise a slow stream would be retried inline (and time out) on every play
            persistNeeded = true;

            // pre-flight 500ms HEAD: a dead host (404/502/timeout) drops straight to the unprobed fallback and is negative-cached,
            // so neither the 5s inline budget nor an ffprobe slot of the 2-slot gate is wasted; a tripped breaker also skips probing
            var probeService = _probeServiceFactory();
            if (probeService.IsCircuitTripped)
            {
                // breaker open: no inline probe. Enqueue the background persist instead; it refuses during the cooldown, and after it
                // the pipeline admits exactly one half-open trial that must fully succeed in ffprobe before the circuit closes
                // the persist queue silently ignores requests refused by an open breaker or an active half-open trial (no failure count,
                // no negative cache), and playback falls back to the raw .strm URL immediately via the unprobed `resolved` sources
                probeService.EnqueueBackgroundPersist(item);
                return resolved;
            }

            if (!await probeService.PreflightAsync(item, cancellationToken).ConfigureAwait(false))
            {
                return resolved;
            }

            // protects the Core i3 from latency spikes: every source shares ONE 5s budget and the 2-process gate, and the
            // client's token is passed through, so playback start can never wait on unbounded or unlimited ffprobe work
            var infos = await probeService
                .ProbeSourcesInlineAsync(targets, cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < targets.Count; i++)
            {
                if (infos[i] is null)
                {
                    // protects playback: a source that timed out simply stays unprobed so the client resolves it natively
                    _logger.LogWarning(
                        "QuickSTRM: Inline probe timed out or no slot for source {Id} of {Name}; leaving it unprobed",
                        targets[i].Id,
                        item.Name);
                    continue;
                }

                ApplyProbeResult(item, user, targets[i], infos[i]);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // protects the Core i3: the client already left, so stop immediately instead of doing resolution work nobody needs
            throw;
        }
        catch (Exception ex)
        {
            // protects playback: a probe failure must never become a playback failure; we degrade to the unprobed sources
            _logger.LogWarning(ex, "QuickSTRM: Inline probe failed for {Name}; falling back to unprobed sources", item.Name);
        }

        if (persistNeeded)
        {
            try
            {
                // improves efficiency: fire-and-forget persistence means the NEXT play takes the zero-probe path instead of
                // re-probing in memory every time, and it never blocks the stream that is starting now; the service
                // de-duplicates and bounds the queue, so repeated play attempts cannot flood it
                _probeServiceFactory().EnqueueBackgroundPersist(item);
            }
            catch (Exception ex)
            {
                // protects playback: a persistence problem must never affect the stream that is starting
                _logger.LogWarning(ex, "QuickSTRM: Could not enqueue background persist for {Name}", item.Name);
            }
        }

        // protects the Core i3 from blocking I/O: allowMediaProbe was false for the whole resolution, so Jellyfin never launches
        // a second, uncapped ffprobe; on fallback the client receives the plain .strm URL and resolves the stream natively
        return resolved;
    }

    private static bool HasVideoStream(MediaSourceInfo source)
    {
        if (source.MediaStreams is null)
        {
            return false;
        }

        foreach (var stream in source.MediaStreams)
        {
            if (stream.Type == MediaStreamType.Video)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Maps the probe result onto the source exactly like Jellyfin's own AddMediaInfoWithProbe (10.11) does.
    /// Codec, profile, level, bit depth, channel layout etc. live inside each <see cref="MediaStream"/>, so assigning
    /// MediaStreams carries them; the remaining source-level fields are copied explicitly below.
    /// </summary>
    private void ApplyProbeResult(BaseItem item, User user, MediaSourceInfo source, MediaInfo info)
    {
        // improves client stability: constrained clients (e.g. Fire TV Stick 4K) decide direct-play vs transcode from these
        // container/format/timestamp/3D/video-type flags plus per-stream codec data, so a partial copy would break parsing
        source.Bitrate = info.Bitrate;
        source.Container = info.Container;
        source.Formats = info.Formats;
        source.MediaStreams = info.MediaStreams;
        source.RunTimeTicks = info.RunTimeTicks;
        source.Size = info.Size;
        source.Timestamp = info.Timestamp;
        source.Video3DFormat = info.Video3DFormat;
        source.VideoType = info.VideoType;
        source.DefaultSubtitleStreamIndex = null;

        MediaStream audioStream = null;
        MediaStream videoStream = null;
        foreach (var stream in source.MediaStreams)
        {
            if (audioStream is null && stream.Type == MediaStreamType.Audio)
            {
                audioStream = stream;
            }
            else if (videoStream is null && stream.Type == MediaStreamType.Video)
            {
                videoStream = stream;
            }
        }

        source.DefaultAudioStreamIndex = audioStream is null || audioStream.Index == -1 ? null : audioStream.Index;

        if (videoStream is not null)
        {
            if (!videoStream.BitRate.HasValue)
            {
                // improves client stability: streams without a reported bitrate get the same width-based estimate as the stock
                // path, so bitrate-limit checks on weak clients don't see zero/null and mis-pick direct play
                var width = videoStream.Width ?? 1920;
                if (width >= 3000)
                {
                    videoStream.BitRate = 30000000;
                }
                else if (width >= 1900)
                {
                    videoStream.BitRate = 20000000;
                }
                else if (width >= 1200)
                {
                    videoStream.BitRate = 8000000;
                }
                else if (width >= 700)
                {
                    videoStream.BitRate = 2000000;
                }
            }

            // improves client stability: probe-reported IsAVC is unreliable and blocks stream copy, so it is cleared exactly as
            // Jellyfin does after its own probe (this avoids needless transcodes on a low-end server)
            videoStream.IsAVC = null;
        }

        if (user is not null)
        {
            // keeps client behaviour identical to the stock path: user language preferences pick the default audio/subtitle
            _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
        }

        // improves client stability: derives a total bitrate when the probe omitted one, as the stock path does
        source.InferTotalBitrate(true);
    }


    // ── All remaining members: 100% delegation to _inner ──

    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
        => _inner.AddParts(providers);

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
        => _inner.GetMediaStreams(itemId);

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
        => _inner.GetMediaStreams(query);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId)
        => _inner.GetMediaAttachments(itemId);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query)
        => _inner.GetMediaAttachments(query);

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null)
        => _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

    public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
        => _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);

    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
        => _inner.OpenLiveStream(request, cancellationToken);

    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken)
        => _inner.OpenLiveStreamInternal(request, cancellationToken);

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStream(id, cancellationToken);

    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    public ILiveStream GetLiveStreamInfo(string id)
        => _inner.GetLiveStreamInfo(id);

    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId)
        => _inner.GetLiveStreamInfoByUniqueId(uniqueId);

    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken)
        => _inner.GetRecordingStreamMediaSources(info, cancellationToken);

    public Task CloseLiveStream(string id)
        => _inner.CloseLiveStream(id);

    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStreamMediaInfo(id, cancellationToken);

    public bool SupportsDirectStream(string path, MediaProtocol protocol)
        => _inner.SupportsDirectStream(path, protocol);

    public MediaProtocol GetPathProtocol(string path)
        => _inner.GetPathProtocol(path);

    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
        => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken)
        => _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);

    public void Dispose()
    {
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
