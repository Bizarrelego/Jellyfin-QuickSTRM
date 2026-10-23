#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Core interception point — routing for .strm playback:
    /// 1. Non-.strm → passthrough (allowMediaProbe unchanged)
    /// 2. .strm with cached MediaStreams (at least one Video and one Audio) → return immediately with direct play/stream/transcode enabled
    /// 3. .strm with RunTimeTicks > 0 → allowMediaProbe = false (skip ffprobe)
    /// 4. .strm without stream info → capped inline probe, then resolve without a second probe;
    ///    on timeout/queue starvation fall back to the unprobed .strm source
    /// </summary>
    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        var isStrm = ProbeService.IsStrmFile(item.Path);

        if (!isStrm)
        {
            // improves performance: non-.strm items return the inner task directly, so no async state machine is allocated
            return await _inner.GetPlaybackMediaSources(
                item, user, allowMediaProbe, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
        }

        // Fetch sources from _inner once with allowMediaProbe: false so Jellyfin builds static/dynamic
        // sources without launching an unconstrained ffprobe.
        var sources = (await _inner.GetPlaybackMediaSources(
            item, user, allowMediaProbe: false, enablePathSubstitution, cancellationToken).ConfigureAwait(false)).ToList();

        // Populate MediaStreams from SQLite if available and normalize
        EnsureStreamCapabilities(sources, item.Id);

        var completeSources = sources.Where(HasCompleteMediaStreams).ToList();
        if (completeSources.Count > 0)
        {
            _logger.LogDebug(
                "QuickSTRM: Item {Name} has complete video and audio streams in database; returning MediaSourceInfo immediately without probing",
                item.Name);

            foreach (var s in completeSources)
            {
                NormalizeMediaSourceInfo(s);
            }

            return completeSources;
        }

        if (!allowMediaProbe)
        {
            // CRITICAL: If item does not have complete streams and probing is disallowed,
            // return empty list so Jellyfin's native playback pipeline handles the .strm file natively!
            _logger.LogDebug(
                "QuickSTRM: Item {Name} lacks complete streams and probing is disallowed; falling back to Jellyfin native pipeline",
                item.Name);
            return Array.Empty<MediaSourceInfo>();
        }

        // only this rare first-play path pays for an async state machine, because it must await the capped probe
        return await ProbeThenResolveAsync(item, user, sources, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MediaSourceInfo>> ProbeThenResolveAsync(
        BaseItem item,
        User user,
        List<MediaSourceInfo> resolved,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("QuickSTRM: Inline-probing first-play {Name} (in memory)", item.Name);

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
                    && !HasCompleteMediaStreams(source))
                {
                    targets.Add(source);
                }
            }

            if (targets.Count == 0)
            {
                EnsureStreamCapabilities(resolved, item.Id);
                var validSources = resolved.Where(HasCompleteMediaStreams).ToList();
                if (validSources.Count > 0)
                {
                    foreach (var s in validSources)
                    {
                        NormalizeMediaSourceInfo(s);
                    }
                    return validSources;
                }
                return Array.Empty<MediaSourceInfo>();
            }

            // improves efficiency: from here on the item is unprobed in the database, so it must be persisted whether the inline
            // probe succeeds, times out or fails; otherwise a slow stream would be retried inline (and time out) on every play
            persistNeeded = true;

            // pre-flight HEAD: a dead host (404/502/timeout) drops straight to the unprobed fallback and is negative-cached,
            // so neither the inline budget nor an ffprobe slot of the 2-slot gate is wasted; a tripped breaker also skips probing
            var probeService = _probeServiceFactory();
            if (probeService.IsCircuitTripped)
            {
                // breaker open: no inline probe. Enqueue the background persist instead; it refuses during the cooldown, and after it
                // the pipeline admits exactly one half-open trial that must fully succeed in ffprobe before the circuit closes
                probeService.EnqueueBackgroundPersist(item, highPriority: true);
                EnsureStreamCapabilities(resolved, item.Id);
                var validSources = resolved.Where(HasCompleteMediaStreams).ToList();
                if (validSources.Count > 0)
                {
                    foreach (var s in validSources)
                    {
                        NormalizeMediaSourceInfo(s);
                    }
                    return validSources;
                }
                return Array.Empty<MediaSourceInfo>();
            }

            if (!await probeService.PreflightAsync(item, cancellationToken).ConfigureAwait(false))
            {
                EnsureStreamCapabilities(resolved, item.Id);
                var validSources = resolved.Where(HasCompleteMediaStreams).ToList();
                if (validSources.Count > 0)
                {
                    foreach (var s in validSources)
                    {
                        NormalizeMediaSourceInfo(s);
                    }
                    return validSources;
                }
                return Array.Empty<MediaSourceInfo>();
            }

            // protects the Core i3 from latency spikes: every source shares ONE budget and the 2-process gate, and the
            // client's token is passed through, so playback start can never wait on unbounded or unlimited ffprobe work
            var infos = await probeService
                .ProbeSourcesInlineAsync(targets, cancellationToken, item)
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
                _probeServiceFactory().EnqueueBackgroundPersist(item, highPriority: true);
            }
            catch (Exception ex)
            {
                // protects playback: a persistence problem must never affect the stream that is starting
                _logger.LogWarning(ex, "QuickSTRM: Could not enqueue background persist for {Name}", item.Name);
            }
        }

        EnsureStreamCapabilities(resolved, item.Id);
        var complete = resolved.Where(HasCompleteMediaStreams).ToList();
        if (complete.Count > 0)
        {
            foreach (var s in complete)
            {
                NormalizeMediaSourceInfo(s);
            }
            return complete;
        }

        _logger.LogInformation(
            "QuickSTRM: Item {Name} does not have complete video and audio streams; returning empty sources so Jellyfin native playback pipeline resolves .strm natively",
            item.Name);
        return Array.Empty<MediaSourceInfo>();
    }

    public static string NormalizeContainer(string container, string path = null)
    {
        var input = container?.Trim();
        if (string.Equals(input, "strm", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(input))
        {
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    var uriPath = Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.AbsolutePath : path;
                    var ext = System.IO.Path.GetExtension(uriPath).TrimStart('.').Trim();
                    if (!string.IsNullOrEmpty(ext) && !ext.Equals("strm", StringComparison.OrdinalIgnoreCase))
                    {
                        input = ext;
                    }
                }
                catch
                {
                }
            }
        }

        if (string.IsNullOrWhiteSpace(input) || string.Equals(input, "strm", StringComparison.OrdinalIgnoreCase))
        {
            return "mp4";
        }

        if (input.Contains("mp4", StringComparison.OrdinalIgnoreCase) || input.Contains("mov", StringComparison.OrdinalIgnoreCase))
        {
            return "mp4";
        }

        if (input.Contains("matroska", StringComparison.OrdinalIgnoreCase) || input.Contains("mkv", StringComparison.OrdinalIgnoreCase))
        {
            return "mkv";
        }

        if (input.Contains("webm", StringComparison.OrdinalIgnoreCase))
        {
            return "webm";
        }

        if (input.Contains(','))
        {
            var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (part.Contains("mp4", StringComparison.OrdinalIgnoreCase) || part.Contains("mov", StringComparison.OrdinalIgnoreCase))
                {
                    return "mp4";
                }

                if (part.Contains("matroska", StringComparison.OrdinalIgnoreCase) || part.Contains("mkv", StringComparison.OrdinalIgnoreCase))
                {
                    return "mkv";
                }

                if (part.Contains("webm", StringComparison.OrdinalIgnoreCase))
                {
                    return "webm";
                }
            }

            var first = parts.FirstOrDefault();
            if (!string.IsNullOrEmpty(first) && !string.Equals(first, "strm", StringComparison.OrdinalIgnoreCase))
            {
                return first;
            }

            return "mp4";
        }

        return input;
    }

    public static void NormalizeMediaSourceInfo(MediaSourceInfo source)
    {
        if (source is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(source.Path) &&
            (source.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             source.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            source.Protocol = MediaProtocol.Http;
        }
        else
        {
            source.Protocol = MediaProtocol.File;
        }

        source.Container = NormalizeContainer(source.Container, source.Path);
        source.RequiresOpening = false;
        source.SupportsProbing = true;

        if (source.MediaStreams is not null && source.MediaStreams.Count > 0)
        {
            var firstAudio = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
            if (firstAudio is not null && firstAudio.Index >= 0)
            {
                source.DefaultAudioStreamIndex = firstAudio.Index;
            }

            var firstSubtitle = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle);
            if (firstSubtitle is not null && firstSubtitle.Index >= 0)
            {
                source.DefaultSubtitleStreamIndex = firstSubtitle.Index;
            }
            else
            {
                source.DefaultSubtitleStreamIndex = null;
            }
        }
    }

    public static bool HasCompleteMediaStreams(MediaSourceInfo source)
    {
        if (source?.MediaStreams is null || source.MediaStreams.Count == 0)
        {
            return false;
        }

        var hasVideo = false;
        var hasAudio = false;

        for (var i = 0; i < source.MediaStreams.Count; i++)
        {
            var stream = source.MediaStreams[i];
            if (stream.Type == MediaStreamType.Video && !string.IsNullOrWhiteSpace(stream.Codec))
            {
                hasVideo = true;
            }
            else if (stream.Type == MediaStreamType.Audio && !string.IsNullOrWhiteSpace(stream.Codec))
            {
                hasAudio = true;
            }

            if (hasVideo && hasAudio)
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureStreamCapabilities(MediaSourceInfo source, Guid? itemId = null)
    {
        if (source is null)
        {
            return;
        }

        // Only query SQLite if source.MediaStreams is not already populated
        if ((source.MediaStreams == null || source.MediaStreams.Count == 0) && itemId.HasValue)
        {
            try
            {
                var streams = _inner.GetMediaStreams(itemId.Value);
                if (streams is not null && streams.Count > 0)
                {
                    source.MediaStreams = streams is List<MediaStream> list ? list : new List<MediaStream>(streams);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "QuickSTRM: Could not retrieve media streams for item {ItemId}", itemId.Value);
            }
        }

        if (HasCompleteMediaStreams(source))
        {
            NormalizeMediaSourceInfo(source);
            source.SupportsDirectPlay = true;
            source.SupportsDirectStream = true;
            source.SupportsTranscoding = true;
        }
    }

    private void EnsureStreamCapabilities(IEnumerable<MediaSourceInfo> sources, Guid? itemId = null)
    {
        if (sources is null)
        {
            return;
        }

        foreach (var source in sources)
        {
            EnsureStreamCapabilities(source, itemId);
        }
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
        source.Container = NormalizeContainer(info.Container, source.Path);
        source.Formats = info.Formats;
        source.MediaStreams = info.MediaStreams;
        source.RunTimeTicks = info.RunTimeTicks;
        source.Size = info.Size;
        source.Timestamp = info.Timestamp;
        source.Video3DFormat = info.Video3DFormat;
        source.VideoType = info.VideoType;

        NormalizeMediaSourceInfo(source);

        if (HasCompleteMediaStreams(source))
        {
            source.SupportsDirectPlay = true;
            source.SupportsDirectStream = true;
            source.SupportsTranscoding = true;
        }

        MediaStream audioStream = null;
        MediaStream videoStream = null;
        if (source.MediaStreams is not null)
        {
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


    // ── All remaining members: delegation to _inner ──

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
    {
        var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
        if (ProbeService.IsStrmFile(item?.Path))
        {
            EnsureStreamCapabilities(sources, item?.Id);
        }
        return sources;
    }

    public async Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
    {
        var source = await _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
        if (source is not null && ProbeService.IsStrmFile(item?.Path))
        {
            EnsureStreamCapabilities(source, item?.Id);
        }
        return source;
    }

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
