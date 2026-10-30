#nullable disable

using System;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<Guid, DateTime> _recentInlineProbeAttempts = new();

    internal static void ClearRecentInlineProbeAttempts() => _recentInlineProbeAttempts.Clear();

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

        if (sources.Count == 0)
        {
            try
            {
                var staticSources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
                if (staticSources is not null && staticSources.Count > 0)
                {
                    sources.AddRange(staticSources);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "QuickSTRM: Could not retrieve static media sources from inner manager for {Name}", item.Name);
            }
        }

        string targetUrl = null;
        try
        {
            targetUrl = ProbeService.ReadStrmTargetFast(item.Path) ?? await ProbeService.ReadStrmUrlAsync(item.Path, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            targetUrl = ProbeService.ReadStrmTargetFast(item.Path);
        }

        var effectivePath = !string.IsNullOrWhiteSpace(targetUrl) ? targetUrl : item.Path;
        var isHttp = effectivePath?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true ||
                     effectivePath?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;

        if (sources.Count == 0)
        {
            _logger.LogInformation(
                "QuickSTRM: Inner media source manager returned empty sources for {Name}; synthesizing fallback native source",
                item.Name);

            var fallbackSource = new MediaSourceInfo
            {
                Id = item.Id.ToString("N"),
                Path = effectivePath,
                Protocol = isHttp ? MediaProtocol.Http : MediaProtocol.File,
                Container = NormalizeContainer(item.Container, effectivePath),
                MediaStreams = new List<MediaStream>(),
                Type = MediaSourceType.Default,
                SupportsProbing = true,
                RequiresOpening = false,
                RequiresClosing = false,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                IsRemote = isHttp,
                ReadAtNativeFramerate = false,
                RunTimeTicks = item.RunTimeTicks,
                Bitrate = 15_000_000
            };

            sources.Add(fallbackSource);
        }

        // Check in-memory ProbeService cache FIRST!
        var probeService = _probeServiceFactory?.Invoke();
        CachedProbeResult cachedProbe = null;
        if (probeService is not null && item.Id != Guid.Empty)
        {
            probeService.CancelPrewarm(item.Id);
            probeService.TryGetCachedProbeResult(item.Id, out cachedProbe);
        }

        IReadOnlyList<MediaStream> itemStreams = null;
        if (cachedProbe is not null && HasValidVideoStream(cachedProbe.MediaStreams))
        {
            itemStreams = cachedProbe.MediaStreams;
        }
        else
        {
            itemStreams = GetItemStreams(item);
        }

        if (!HasValidVideoStream(itemStreams))
        {
            var existingSourceStreams = sources.FirstOrDefault(s => s.MediaStreams != null && s.MediaStreams.Count > 0)?.MediaStreams;
            if (HasValidVideoStream(existingSourceStreams))
            {
                itemStreams = existingSourceStreams;
            }
        }

        if (!HasValidVideoStream(itemStreams))
        {
            var now = DateTime.UtcNow;
            var onCooldown = false;
            if (item.Id != Guid.Empty && _recentInlineProbeAttempts.TryGetValue(item.Id, out var lastAttempt))
            {
                var elapsed = now - lastAttempt;
                var cooldownWindow = allowMediaProbe ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(15);
                if (elapsed < cooldownWindow)
                {
                    onCooldown = true;
                    _logger.LogDebug(
                        "QuickSTRM: Item {Name} probe cooldown active ({Elapsed:0.0}s < 30s); skipping redundant inline probe",
                        item.Name,
                        elapsed.TotalSeconds);
                }
            }

            if (!onCooldown)
            {
                if (item.Id != Guid.Empty)
                {
                    _recentInlineProbeAttempts[item.Id] = now;
                }

                _logger.LogInformation(
                    "QuickSTRM: Item {Name} has null/empty streams or missing valid video codec; triggering on-demand inline probe",
                    item.Name);

                MediaInfo probeResult = null;
                if (probeService is not null)
                {
                    try
                    {
                        probeResult = await probeService.ProbeOneInlineAsync(item, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "QuickSTRM: On-demand inline probe failed for {Name}", item.Name);
                    }
                }

                // Re-evaluate item streams after inline probe: prefer direct probe result if valid, else check database/memory cache
                if (probeResult?.MediaStreams is not null && HasValidVideoStream(probeResult.MediaStreams))
                {
                    itemStreams = probeResult.MediaStreams;
                }
                else if (item.Id != Guid.Empty && probeService != null && probeService.TryGetCachedProbeResult(item.Id, out var newlyCached) && HasValidVideoStream(newlyCached.MediaStreams))
                {
                    itemStreams = newlyCached.MediaStreams;
                    cachedProbe = newlyCached;
                }
                else
                {
                    itemStreams = GetItemStreams(item);
                }

                if (!HasValidVideoStream(itemStreams))
                {
                    _logger.LogWarning(
                        "QuickSTRM: On-demand probe failed or streams remain unprobed for {Name}; continuing with sanitized native source",
                        item.Name);
                }
            }
        }

        // Populate MediaStreams from SQLite / in-memory cache if available and normalize
        EnsureStreamCapabilities(sources, item.Id, itemStreams);

        foreach (var s in sources)
        {
            s.Id = item.Id.ToString("N");
            if (!string.IsNullOrWhiteSpace(targetUrl))
            {
                s.Path = targetUrl;
                s.Protocol = (targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ? MediaProtocol.Http : MediaProtocol.File;
                s.IsRemote = s.Protocol == MediaProtocol.Http;
            }

            if (cachedProbe?.RunTimeTicks.HasValue == true && cachedProbe.RunTimeTicks.Value > 0)
            {
                s.RunTimeTicks = cachedProbe.RunTimeTicks.Value;
            }
            else if (item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0)
            {
                s.RunTimeTicks = item.RunTimeTicks.Value;
            }

            if (cachedProbe?.Bitrate.HasValue == true && cachedProbe.Bitrate.Value > 0)
            {
                s.Bitrate = (int)Math.Min(cachedProbe.Bitrate.Value, int.MaxValue);
            }

            if (cachedProbe?.Size.HasValue == true && cachedProbe.Size.Value > 0)
            {
                s.Size = cachedProbe.Size.Value;
            }
            else if (item.Size.HasValue && item.Size.Value > 0)
            {
                s.Size = item.Size.Value;
            }

            s.Container = NormalizeContainer(cachedProbe?.Container ?? s.Container ?? item.Container, s.Path);

            NormalizeMediaSourceInfo(s);
            if (user is not null)
            {
                _inner.SetDefaultAudioAndSubtitleStreamIndices(item, s, user);
            }
            SanitizeDefaultStreamIndices(s);
        }

        var completeSources = sources.Where(HasCompleteMediaStreams).ToList();
        if (completeSources.Count > 0)
        {
            _logger.LogDebug(
                "QuickSTRM: Item {Name} has complete video and audio streams; returning normalized MediaSourceInfo",
                item.Name);
            return completeSources;
        }

        if (!allowMediaProbe)
        {
            _logger.LogDebug(
                "QuickSTRM: Item {Name} unprobed or probing disallowed; returning sanitized native sources to prevent transcoder crash",
                item.Name);
            return sources;
        }

        // only this rare first-play path pays for an async state machine, because it must await the capped probe
        if (probeService is not null && !HasValidVideoStream(itemStreams))
        {
            try
            {
                probeService.EnqueueBackgroundPersist(item, highPriority: true);
            }
            catch
            {
            }
        }

        return sources;
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
                foreach (var s in resolved)
                {
                    NormalizeMediaSourceInfo(s);
                    if (user is not null)
                    {
                        _inner.SetDefaultAudioAndSubtitleStreamIndices(item, s, user);
                    }
                    SanitizeDefaultStreamIndices(s);
                }
                var validSources = resolved.Where(HasCompleteMediaStreams).ToList();
                return validSources.Count > 0 ? validSources : resolved;
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
                foreach (var s in resolved)
                {
                    NormalizeMediaSourceInfo(s);
                    if (user is not null)
                    {
                        _inner.SetDefaultAudioAndSubtitleStreamIndices(item, s, user);
                    }
                    SanitizeDefaultStreamIndices(s);
                }
                var validSources = resolved.Where(HasCompleteMediaStreams).ToList();
                return validSources.Count > 0 ? validSources : resolved;
            }

            if (!await probeService.PreflightAsync(item, cancellationToken).ConfigureAwait(false))
            {
                EnsureStreamCapabilities(resolved, item.Id);
                foreach (var s in resolved)
                {
                    NormalizeMediaSourceInfo(s);
                    if (user is not null)
                    {
                        _inner.SetDefaultAudioAndSubtitleStreamIndices(item, s, user);
                    }
                    SanitizeDefaultStreamIndices(s);
                }
                var validSources = resolved.Where(HasCompleteMediaStreams).ToList();
                return validSources.Count > 0 ? validSources : resolved;
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
        foreach (var s in resolved)
        {
            NormalizeMediaSourceInfo(s);
            if (user is not null)
            {
                _inner.SetDefaultAudioAndSubtitleStreamIndices(item, s, user);
            }
            SanitizeDefaultStreamIndices(s);
        }

        var complete = resolved.Where(HasCompleteMediaStreams).ToList();
        if (complete.Count > 0)
        {
            return complete;
        }

        _logger.LogInformation(
            "QuickSTRM: Item {Name} does not have complete video and audio streams; returning sanitized native sources to prevent transcoder crash",
            item.Name);
        return resolved;
    }

    private static string ExtractQueryExtensionFromUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Query))
            {
                var query = uri.Query.AsSpan();
                string[] paramNames = ["extension=", "format=", "ext="];
                foreach (var param in paramNames)
                {
                    var idx = 0;
                    while (idx < query.Length)
                    {
                        var matchIdx = query.Slice(idx).IndexOf(param.AsSpan(), StringComparison.OrdinalIgnoreCase);
                        if (matchIdx < 0)
                        {
                            break;
                        }

                        var absMatchIdx = idx + matchIdx;
                        if (absMatchIdx == 0 || query[absMatchIdx - 1] == '?' || query[absMatchIdx - 1] == '&')
                        {
                            var slice = query.Slice(absMatchIdx + param.Length);
                            var ampIdx = slice.IndexOf('&');
                            if (ampIdx >= 0)
                            {
                                slice = slice.Slice(0, ampIdx);
                            }

                            slice = slice.TrimStart('.');
                            if (!slice.IsEmpty)
                            {
                                var ext = slice.ToString();
                                if (!ext.Equals("strm", StringComparison.OrdinalIgnoreCase))
                                {
                                    return ext;
                                }
                            }
                        }

                        idx = absMatchIdx + param.Length;
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ExtractExtensionFromPathOrUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var queryExt = ExtractQueryExtensionFromUrl(path);
        if (!string.IsNullOrEmpty(queryExt))
        {
            return queryExt;
        }

        try
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                var pathExt = System.IO.Path.GetExtension(uri.AbsolutePath).TrimStart('.').Trim();
                if (!string.IsNullOrEmpty(pathExt) && !pathExt.Equals("strm", StringComparison.OrdinalIgnoreCase))
                {
                    return pathExt;
                }
            }
            else
            {
                var localExt = System.IO.Path.GetExtension(path).TrimStart('.').Trim();
                if (!string.IsNullOrEmpty(localExt) && !localExt.Equals("strm", StringComparison.OrdinalIgnoreCase))
                {
                    return localExt;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    public static bool IsLocalOrPrivateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1", StringComparison.Ordinal) ||
            host.Equals("::1", StringComparison.Ordinal) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4)
            {
                // 10.0.0.0/8
                if (bytes[0] == 10) return true;
                // 172.16.0.0/12
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                // 127.0.0.0/8
                if (bytes[0] == 127) return true;
            }
        }

        return false;
    }

    public static string NormalizeContainer(string container, string path = null)
    {
        // URL query parameter (?extension=mp4 or &extension=mkv) absolutely overrides any internal FFmpeg comma-separated container string
        var queryOverride = ExtractQueryExtensionFromUrl(path);
        var input = !string.IsNullOrEmpty(queryOverride) ? queryOverride : container?.Trim();

        if (string.Equals(input, "strm", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(input))
        {
            var extracted = ExtractExtensionFromPathOrUrl(path);
            if (!string.IsNullOrEmpty(extracted))
            {
                input = extracted;
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

    public static string NormalizeCodec(string codec, MediaStreamType streamType)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return codec;
        }

        var c = codec.Trim().ToLowerInvariant();
        if (streamType == MediaStreamType.Video)
        {
            if (c.Contains("hevc", StringComparison.Ordinal) ||
                c.Contains("h265", StringComparison.Ordinal) ||
                c.Contains("x265", StringComparison.Ordinal) ||
                c.Contains("hev1", StringComparison.Ordinal) ||
                c.Contains("hvc1", StringComparison.Ordinal))
            {
                return "hevc";
            }

            if (c.Contains("av1", StringComparison.Ordinal) ||
                c.Contains("av01", StringComparison.Ordinal))
            {
                return "av1";
            }

            if (c.Contains("h264", StringComparison.Ordinal) ||
                c.Contains("avc", StringComparison.Ordinal) ||
                c.Contains("x264", StringComparison.Ordinal))
            {
                return "h264";
            }

            var spaceOrParen = c.IndexOfAny([' ', '(']);
            return spaceOrParen > 0 ? c[..spaceOrParen] : c;
        }

        if (streamType == MediaStreamType.Audio)
        {
            if (c.Contains("eac3", StringComparison.Ordinal) ||
                c.Contains("e-ac-3", StringComparison.Ordinal) ||
                c.Contains("e-ac3", StringComparison.Ordinal) ||
                c.Contains("ec-3", StringComparison.Ordinal) ||
                c.Contains("ec3", StringComparison.Ordinal) ||
                c.Contains("dd+", StringComparison.Ordinal) ||
                c.Contains("ddp", StringComparison.Ordinal))
            {
                return "eac3";
            }

            if (c.Contains("ac3", StringComparison.Ordinal) ||
                c.Contains("ac-3", StringComparison.Ordinal) ||
                c.Contains("a52", StringComparison.Ordinal))
            {
                return "ac3";
            }

            if (c.Contains("aac", StringComparison.Ordinal) ||
                c.Contains("mp4a", StringComparison.Ordinal))
            {
                return "aac";
            }

            var spaceOrParen = c.IndexOfAny([' ', '(']);
            return spaceOrParen > 0 ? c[..spaceOrParen] : c;
        }

        var idx = c.IndexOfAny([' ', '(']);
        return idx > 0 ? c[..idx] : c;
    }

    /// <summary>
    /// Enriches a <see cref="MediaStream"/> with normalized codec, sequential index, item association,
    /// channel layout, and synthesized <see cref="MediaStream.DisplayTitle"/> so Jellyfin Web and UI plugins
    /// such as Jellyfin Enhanced display resolution, HDR, codec, and audio channel badges accurately.
    /// </summary>
    public static void EnrichMediaStream(MediaStream stream, int index, Guid itemId = default)
    {
        if (stream is null)
        {
            return;
        }

        stream.Index = index;

        if (!string.IsNullOrWhiteSpace(stream.Codec))
        {
            stream.Codec = NormalizeCodec(stream.Codec, stream.Type);
        }

        if (stream.Type == MediaStreamType.Audio)
        {
            if (!stream.Channels.HasValue || stream.Channels.Value <= 0)
            {
                if (!string.IsNullOrWhiteSpace(stream.ChannelLayout))
                {
                    if (stream.ChannelLayout.Contains("7.1", StringComparison.OrdinalIgnoreCase))
                    {
                        stream.Channels = 8;
                    }
                    else if (stream.ChannelLayout.Contains("5.1", StringComparison.OrdinalIgnoreCase))
                    {
                        stream.Channels = 6;
                    }
                    else if (stream.ChannelLayout.Contains("stereo", StringComparison.OrdinalIgnoreCase) ||
                             stream.ChannelLayout.Contains("2.0", StringComparison.OrdinalIgnoreCase))
                    {
                        stream.Channels = 2;
                    }
                    else if (stream.ChannelLayout.Contains("mono", StringComparison.OrdinalIgnoreCase) ||
                             stream.ChannelLayout.Contains("1.0", StringComparison.OrdinalIgnoreCase))
                    {
                        stream.Channels = 1;
                    }
                    else
                    {
                        stream.Channels = 2;
                    }
                }
                else
                {
                    stream.Channels = 2;
                }
            }

            if (string.IsNullOrWhiteSpace(stream.ChannelLayout) && stream.Channels.HasValue)
            {
                stream.ChannelLayout = stream.Channels.Value switch
                {
                    1 => "mono",
                    2 => "stereo",
                    6 => "5.1",
                    8 => "7.1",
                    _ => $"{stream.Channels.Value}ch"
                };
            }
        }
    }

    public static void NormalizeMediaSourceInfo(MediaSourceInfo source)
    {
        if (source is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(source.Path) &&
            (source.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             source.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
             ProbeService.IsStrmFile(source.Path)))
        {
            source.Protocol = MediaProtocol.Http;
            source.IsRemote = true;
        }
        if (!string.IsNullOrEmpty(source.Path) && ProbeService.IsStrmFile(source.Path))
        {
            var fastTarget = ProbeService.ReadStrmTargetFast(source.Path);
            if (!string.IsNullOrWhiteSpace(fastTarget))
            {
                source.Path = fastTarget;
                var isFastHttp = fastTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 fastTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                source.Protocol = isFastHttp ? MediaProtocol.Http : MediaProtocol.File;
                source.IsRemote = isFastHttp;
            }
        }

        if (source.Protocol == MediaProtocol.Http)
        {
            source.IsRemote = true;
        }
        else
        {
            source.Protocol = MediaProtocol.File;
            source.IsRemote = false;
        }

        source.Container = NormalizeContainer(source.Container, source.Path);
        source.RequiresOpening = false;
        source.RequiresClosing = false;
        source.SupportsProbing = true;
        source.SupportsDirectPlay = true;
        source.SupportsDirectStream = true;
        source.SupportsTranscoding = true;
        source.ReadAtNativeFramerate = false;

        var totalStreamBitrate = 0;
        if (source.MediaStreams is not null && source.MediaStreams.Count > 0)
        {
            for (var i = 0; i < source.MediaStreams.Count; i++)
            {
                var s = source.MediaStreams[i];
                EnrichMediaStream(s, i);

                if (s.BitRate.HasValue && s.BitRate.Value > 0)
                {
                    totalStreamBitrate += s.BitRate.Value;
                }
            }
        }

        if (!source.Bitrate.HasValue || source.Bitrate.Value <= 0)
        {
            source.Bitrate = totalStreamBitrate > 0 ? totalStreamBitrate : 15_000_000;
        }

        if (source.MediaStreams is not null)
        {
            foreach (var stream in source.MediaStreams)
            {
                if (stream.Type == MediaStreamType.Video)
                {
                    if (!stream.BitRate.HasValue || stream.BitRate.Value <= 0)
                    {
                        stream.BitRate = source.Bitrate ?? 15_000_000;
                    }
                }
            }
        }

        SanitizeDefaultStreamIndices(source);
    }

    /// <summary>
    /// Ensures that DefaultAudioStreamIndex and DefaultSubtitleStreamIndex point to valid,
    /// in-bounds indices in MediaStreams, and normalizes stream.Index = i.
    /// This definitively prevents ArgumentOutOfRangeException during HLS playlist generation and transcoding.
    /// </summary>
    public static void SanitizeDefaultStreamIndices(MediaSourceInfo source)
    {
        if (source is null)
        {
            return;
        }

        if (source.MediaStreams is null || source.MediaStreams.Count == 0)
        {
            source.DefaultAudioStreamIndex = null;
            source.DefaultSubtitleStreamIndex = null;
            return;
        }

        // 1. Resolve selected audio and subtitle streams based on current pointers or fallbacks
        MediaStream selectedAudio = null;
        if (source.DefaultAudioStreamIndex.HasValue)
        {
            var idx = source.DefaultAudioStreamIndex.Value;
            selectedAudio = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == idx);
        }
        selectedAudio ??= source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)
                          ?? source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);

        var subtitleExplicitlyDisabled = source.DefaultSubtitleStreamIndex.HasValue && source.DefaultSubtitleStreamIndex.Value < 0;
        MediaStream selectedSubtitle = null;
        if (!subtitleExplicitlyDisabled)
        {
            if (source.DefaultSubtitleStreamIndex.HasValue)
            {
                var idx = source.DefaultSubtitleStreamIndex.Value;
                selectedSubtitle = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == idx);
            }
            selectedSubtitle ??= source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.IsDefault)
                                 ?? source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle);
        }

        // 2. Ensure all stream indices in the collection are strictly sequential and 0-based
        for (var i = 0; i < source.MediaStreams.Count; i++)
        {
            source.MediaStreams[i].Index = i;
        }

        // 3. Assign verified, in-bounds indices
        source.DefaultAudioStreamIndex = selectedAudio is not null && selectedAudio.Index >= 0 && selectedAudio.Index < source.MediaStreams.Count
            ? selectedAudio.Index
            : null;

        source.DefaultSubtitleStreamIndex = subtitleExplicitlyDisabled
            ? -1
            : (selectedSubtitle is not null && selectedSubtitle.Index >= 0 && selectedSubtitle.Index < source.MediaStreams.Count
                ? selectedSubtitle.Index
                : null);
    }

    public static bool HasValidVideoStream(IReadOnlyList<MediaStream> streams)
    {
        if (streams is null || streams.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < streams.Count; i++)
        {
            var stream = streams[i];
            if (stream.Type == MediaStreamType.Video && !string.IsNullOrWhiteSpace(stream.Codec))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasCompleteMediaStreams(IReadOnlyList<MediaStream> streams)
    {
        if (streams is null || streams.Count == 0)
        {
            return false;
        }

        var hasVideo = false;
        var hasAudio = false;

        for (var i = 0; i < streams.Count; i++)
        {
            var stream = streams[i];
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

    public static bool HasCompleteMediaStreams(MediaSourceInfo source)
        => HasCompleteMediaStreams(source?.MediaStreams);

    private IReadOnlyList<MediaStream> GetItemStreams(BaseItem item)
    {
        if (item is null)
        {
            return Array.Empty<MediaStream>();
        }

        var probeService = _probeServiceFactory?.Invoke();
        if (probeService is not null && item.Id != Guid.Empty)
        {
            if (probeService.TryGetCachedProbeResult(item.Id, out var cached) && HasValidVideoStream(cached.MediaStreams))
            {
                return cached.MediaStreams;
            }

            if (probeService.InMemoryStreams.TryGetValue(item.Id, out var memStreams) && HasValidVideoStream(memStreams))
            {
                return memStreams;
            }
        }

        try
        {
            var streams = item.GetMediaStreams();
            if (streams is not null && HasValidVideoStream(streams))
            {
                return streams;
            }
        }
        catch
        {
        }

        try
        {
            var streams = _inner.GetMediaStreams(item.Id);
            if (streams is not null && HasValidVideoStream(streams))
            {
                return streams;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "QuickSTRM: Could not retrieve media streams for item {ItemId}", item.Id);
        }

        return Array.Empty<MediaStream>();
    }

    private void EnsureStreamCapabilities(MediaSourceInfo source, Guid? itemId = null, IReadOnlyList<MediaStream> fallbackStreams = null)
    {
        if (source is null)
        {
            return;
        }

        var probeService = _probeServiceFactory?.Invoke();
        CachedProbeResult cachedProbe = null;
        if (probeService is not null && itemId.HasValue && itemId.Value != Guid.Empty)
        {
            probeService.TryGetCachedProbeResult(itemId.Value, out cachedProbe);
        }

        if (cachedProbe is not null)
        {
            if (cachedProbe.RunTimeTicks.HasValue && cachedProbe.RunTimeTicks.Value > 0)
            {
                source.RunTimeTicks = cachedProbe.RunTimeTicks.Value;
            }
            if (cachedProbe.Bitrate.HasValue && cachedProbe.Bitrate.Value > 0)
            {
                source.Bitrate = (int)Math.Min(cachedProbe.Bitrate.Value, int.MaxValue);
            }
            if (cachedProbe.Size.HasValue && cachedProbe.Size.Value > 0)
            {
                source.Size = cachedProbe.Size.Value;
            }
            if (!string.IsNullOrEmpty(cachedProbe.Container))
            {
                source.Container = NormalizeContainer(cachedProbe.Container, source.Path);
            }
        }

        // Populate video/audio streams if source lacks complete video+audio streams
        if (source.MediaStreams == null || source.MediaStreams.Count == 0 || !HasCompleteMediaStreams(source.MediaStreams))
        {
            var existingSubtitles = source.MediaStreams?.Where(s => s.Type == MediaStreamType.Subtitle).ToList();

            IReadOnlyList<MediaStream> targetStreams = null;
            if (fallbackStreams is not null && HasValidVideoStream(fallbackStreams))
            {
                targetStreams = fallbackStreams;
            }
            else if (cachedProbe is not null && HasValidVideoStream(cachedProbe.MediaStreams))
            {
                targetStreams = cachedProbe.MediaStreams;
            }
            else if (itemId.HasValue && itemId.Value != Guid.Empty && probeService is not null)
            {
                if (probeService.InMemoryStreams.TryGetValue(itemId.Value, out var memStreams) && HasValidVideoStream(memStreams))
                {
                    targetStreams = memStreams;
                }
                else
                {
                    try
                    {
                        var streams = _inner.GetMediaStreams(itemId.Value);
                        if (streams is not null && HasValidVideoStream(streams))
                        {
                            targetStreams = streams;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "QuickSTRM: Could not retrieve media streams for item {ItemId}", itemId.Value);
                    }
                }
            }

            if (targetStreams is not null && targetStreams.Count > 0 &&
                (source.MediaStreams == null || !HasValidVideoStream(source.MediaStreams) || HasCompleteMediaStreams(targetStreams)))
            {
                var merged = new List<MediaStream>(targetStreams);
                if (existingSubtitles is not null && existingSubtitles.Count > 0)
                {
                    foreach (var sub in existingSubtitles)
                    {
                        if (!merged.Any(m => m.Type == MediaStreamType.Subtitle && m.Index == sub.Index))
                        {
                            merged.Add(sub);
                        }
                    }
                }
                source.MediaStreams = merged;
            }
        }

        NormalizeMediaSourceInfo(source);
    }

    private void EnsureStreamCapabilities(IEnumerable<MediaSourceInfo> sources, Guid? itemId = null, IReadOnlyList<MediaStream> fallbackStreams = null)
    {
        if (sources is null)
        {
            return;
        }

        foreach (var source in sources)
        {
            EnsureStreamCapabilities(source, itemId, fallbackStreams);
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

        SanitizeDefaultStreamIndices(source);

        // improves client stability: derives a total bitrate when the probe omitted one, as the stock path does
        source.InferTotalBitrate(true);
    }


    // ── All remaining members: delegation to _inner ──

    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
        => _inner.AddParts(providers);

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
    {
        var streams = _inner.GetMediaStreams(itemId);
        if (itemId == Guid.Empty)
        {
            return streams;
        }

        var probeService = _probeServiceFactory?.Invoke();
        var item = probeService?.LibraryManager?.GetItemById(itemId);
        if (item is null || !ProbeService.IsStrmFile(item.Path))
        {
            return streams;
        }

        return EnrichAndFallbackStreams(itemId, streams);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
    {
        var streams = _inner.GetMediaStreams(query);
        if (query is null || query.ItemId == Guid.Empty)
        {
            return streams;
        }

        var probeService = _probeServiceFactory?.Invoke();
        var item = probeService?.LibraryManager?.GetItemById(query.ItemId);
        if (item is null || !ProbeService.IsStrmFile(item.Path))
        {
            return streams;
        }

        if (!query.Type.HasValue && !query.Index.HasValue)
        {
            return EnrichAndFallbackStreams(query.ItemId, streams);
        }

        if (streams is not null && streams.Count > 0)
        {
            for (var i = 0; i < streams.Count; i++)
            {
                EnrichMediaStream(streams[i], streams[i].Index >= 0 ? streams[i].Index : i, query.ItemId);
            }
        }

        return streams;
    }

    private IReadOnlyList<MediaStream> EnrichAndFallbackStreams(Guid itemId, IReadOnlyList<MediaStream> innerStreams)
    {
        var target = innerStreams;
        if (itemId != Guid.Empty && (target is null || !HasCompleteMediaStreams(target)))
        {
            var probeService = _probeServiceFactory?.Invoke();
            if (probeService is not null)
            {
                if (probeService.TryGetCachedProbeResult(itemId, out var cached) &&
                    cached?.MediaStreams is not null &&
                    (target is null || !HasValidVideoStream(target) || HasCompleteMediaStreams(cached.MediaStreams)))
                {
                    target = cached.MediaStreams;
                }
                else if (probeService.InMemoryStreams.TryGetValue(itemId, out var memStreams) &&
                         memStreams is not null &&
                         (target is null || !HasValidVideoStream(target) || HasCompleteMediaStreams(memStreams)))
                {
                    target = memStreams;
                }
            }
        }

        if (target is not null && target.Count > 0)
        {
            for (var i = 0; i < target.Count; i++)
            {
                EnrichMediaStream(target[i], i, itemId);
            }
        }

        return target ?? Array.Empty<MediaStream>();
    }

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId)
        => _inner.GetMediaAttachments(itemId);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query)
        => _inner.GetMediaAttachments(query);

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null)
    {
        var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
        if (ProbeService.IsStrmFile(item?.Path))
        {
            if ((sources is null || sources.Count == 0) && item is not null)
            {
                var fallback = new MediaSourceInfo
                {
                    Id = item.Id.ToString("N"),
                    Path = item.Path,
                    Protocol = MediaProtocol.Http,
                    IsRemote = true,
                    Container = NormalizeContainer(item.Container, item.Path),
                    RunTimeTicks = item.RunTimeTicks,
                    Size = item.Size,
                    Bitrate = 15_000_000,
                    MediaStreams = new List<MediaStream>(),
                    Type = MediaSourceType.Default,
                    SupportsProbing = true,
                    RequiresOpening = false,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true
                };
                var list = new List<MediaSourceInfo> { fallback };
                EnsureStreamCapabilities(list, item.Id, GetItemStreams(item));
                foreach (var s in list)
                {
                    NormalizeMediaSourceInfo(s);
                    SanitizeDefaultStreamIndices(s);
                }
                return list;
            }

            string targetUrl = null;
            try
            {
                if (System.IO.File.Exists(item.Path))
                {
                    targetUrl = System.IO.File.ReadAllText(item.Path).Trim();
                }
            }
            catch
            {
            }

            var itemStreams = GetItemStreams(item);
            EnsureStreamCapabilities(sources, item?.Id, itemStreams);
            foreach (var s in sources)
            {
                if (item is not null)
                {
                    s.Id = item.Id.ToString("N");
                    if (!string.IsNullOrWhiteSpace(targetUrl))
                    {
                        s.Path = targetUrl;
                        s.Protocol = (targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ? MediaProtocol.Http : MediaProtocol.File;
                        s.IsRemote = s.Protocol == MediaProtocol.Http;
                    }

                    if ((!s.RunTimeTicks.HasValue || s.RunTimeTicks.Value <= 0) && item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0)
                    {
                        s.RunTimeTicks = item.RunTimeTicks.Value;
                    }
                    if ((!s.Size.HasValue || s.Size.Value <= 0) && item.Size.HasValue && item.Size.Value > 0)
                    {
                        s.Size = item.Size.Value;
                    }
                    if (!string.IsNullOrEmpty(item.Container))
                    {
                        s.Container = NormalizeContainer(s.Container ?? item.Container, s.Path);
                    }
                }
                NormalizeMediaSourceInfo(s);
                SanitizeDefaultStreamIndices(s);
            }
        }
        return sources;
    }

    public async Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
    {
        var source = await _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
        if (source is not null && ProbeService.IsStrmFile(item?.Path))
        {
            if (string.IsNullOrWhiteSpace(source.Path) || source.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    var target = ProbeService.ReadStrmTargetFast(item.Path) ?? await ProbeService.ReadStrmUrlAsync(item.Path, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        source.Path = target;
                        source.Protocol = target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? MediaProtocol.Http : MediaProtocol.File;
                        source.IsRemote = source.Protocol == MediaProtocol.Http;
                    }
                }
                catch
                {
                }
            }
            source.Id = item.Id.ToString("N");
            NormalizeMediaSourceInfo(source);
            EnsureStreamCapabilities(source, item?.Id);
            SanitizeDefaultStreamIndices(source);
        }
        else if (source is null && item is not null && ProbeService.IsStrmFile(item.Path))
        {
            string target = null;
            try
            {
                target = ProbeService.ReadStrmTargetFast(item.Path) ?? await ProbeService.ReadStrmUrlAsync(item.Path, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
            var effectivePath = !string.IsNullOrWhiteSpace(target) ? target : item.Path;
            var isHttp = effectivePath.StartsWith("http", StringComparison.OrdinalIgnoreCase);

            source = new MediaSourceInfo
            {
                Id = item.Id.ToString("N"),
                Path = effectivePath,
                Protocol = isHttp ? MediaProtocol.Http : MediaProtocol.File,
                Container = NormalizeContainer(item.Container, effectivePath),
                MediaStreams = new List<MediaStream>(),
                Type = MediaSourceType.Default,
                SupportsProbing = true,
                RequiresOpening = false,
                RequiresClosing = false,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                IsRemote = isHttp,
                ReadAtNativeFramerate = false,
                RunTimeTicks = item.RunTimeTicks,
                Bitrate = 15_000_000
            };
            NormalizeMediaSourceInfo(source);
            EnsureStreamCapabilities(source, item.Id);
            SanitizeDefaultStreamIndices(source);
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
    {
        if (ProbeService.IsStrmFile(path))
        {
            return true;
        }
        return _inner.SupportsDirectStream(path, protocol);
    }

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
