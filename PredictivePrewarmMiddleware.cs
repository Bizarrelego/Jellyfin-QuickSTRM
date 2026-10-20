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
        @"^/Users/[^/]+/Items/([^/]+)$",
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
        var path = context.Request.Path.Value;

        if (!string.IsNullOrEmpty(path))
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
        if (!string.IsNullOrEmpty(itemId))
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
