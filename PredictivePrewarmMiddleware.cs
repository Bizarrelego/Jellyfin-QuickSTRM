using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace QuickSTRM;

/// <summary>
/// ASP.NET Core middleware intercepting item detail page requests (^/Users/[^/]+/Items/([^/]+)$)
/// to predictively pre-warm VFS mounts and remote streams before playback starts.
/// </summary>
public class PredictivePrewarmMiddleware : IMiddleware
{
    private static readonly Regex ItemDetailRegex = new(
        @"^/(?:Users/[^/]+/)?Items/([0-9a-fA-F]{8}-?(?:[0-9a-fA-F]{4}-?){3}[0-9a-fA-F]{12})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PlaybackPathRegex = new(
        @"/(?:Items|Videos)/([0-9a-fA-F]{8}-?(?:[0-9a-fA-F]{4}-?){3}[0-9a-fA-F]{12})/(?:PlaybackInfo|stream|master\.m3u8|main\.m3u8)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly VfsPrewarmService _prewarmService;
    private readonly ILogger<PredictivePrewarmMiddleware> _logger;

    public PredictivePrewarmMiddleware(
        VfsPrewarmService prewarmService,
        ILogger<PredictivePrewarmMiddleware> logger)
    {
        _prewarmService = prewarmService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        string? itemId = null;
        var path = context.Request.Path.Value ?? string.Empty;

        // 1. Immediately cancel any active pre-warm if this is a playback or PlaybackInfo request
        // so FFmpeg and Jellyfin get 100% exclusive access to the Infinidysk stream without contention
        var isPlaybackRequest = path.Contains("PlaybackInfo", StringComparison.OrdinalIgnoreCase) ||
                                path.Contains("/stream", StringComparison.OrdinalIgnoreCase) ||
                                path.Contains("/Videos/", StringComparison.OrdinalIgnoreCase) ||
                                path.Contains("/Sessions/Playing", StringComparison.OrdinalIgnoreCase);

        if (isPlaybackRequest)
        {
            var pbMatch = PlaybackPathRegex.Match(path);
            if (pbMatch.Success && pbMatch.Groups.Count > 1)
            {
                _prewarmService.CancelPrewarm(pbMatch.Groups[1].Value);
            }
            else if (context.Request.Query.TryGetValue("itemId", out var qItemId) && !string.IsNullOrEmpty(qItemId))
            {
                _prewarmService.CancelPrewarm(qItemId.ToString());
            }
        }

        // 2. Only pre-warm for detail/hover browsing (GET /Users/{u}/Items/{id} or /Items/{id})
        // and NEVER for playback or stream endpoints
        if (!isPlaybackRequest && HttpMethods.IsGet(context.Request.Method))
        {
            var match = ItemDetailRegex.Match(path);
            if (match.Success && match.Groups.Count > 1)
            {
                itemId = match.Groups[1].Value;
            }
        }

        // Pass the request down the pipeline immediately so the Jellyfin UI does not block
        await next(context).ConfigureAwait(false);

        // After the pipeline returns, dispatch a background Task.Run to VfsPrewarmService.PreWarmItemAsync using the extracted itemId
        // Fast-path synchronous check prevents ThreadPool queueing and closure allocations on cache hits
        if (!string.IsNullOrEmpty(itemId) && !_prewarmService.IsRecentlyWarmed(itemId))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _prewarmService.PreWarmItemAsync(itemId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "QuickSTRM: Background predictive pre-warm failed for item {ItemId}", itemId);
                }
            });
        }
    }
}

/// <summary>
/// Startup filter inserting <see cref="PredictivePrewarmMiddleware"/> into the ASP.NET Core
/// application builder pipeline before the MVC routing endpoints.
/// </summary>
public class PredictivePrewarmStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<PredictivePrewarmMiddleware>();
            next(app);
        };
    }
}
