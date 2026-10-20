using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuickSTRM.Configuration;

namespace QuickSTRM;

/// <summary>
/// Hosted background service managing zero-touch *arr (Radarr, Sonarr, Whisparr) integrations,
/// auto-provisioning webhooks, zero-footprint cleanups, and automatic replacement searching on 404 links.
/// </summary>
public sealed class ArrIntegrationService : IHostedService, IDisposable
{
    private const string WebhookNotificationName = "QuickSTRM Auto-Sync";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ArrIntegrationService> _logger;
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _shutdownCts = new();

    public ArrIntegrationService(
        ILibraryManager libraryManager,
        ILogger<ArrIntegrationService> logger,
        IServerApplicationHost? applicationHost = null)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Delay briefly at server start to allow network interfaces and other services to initialize
                await Task.Delay(TimeSpan.FromSeconds(5), _shutdownCts.Token).ConfigureAwait(false);
                await SyncWebhooksWithRetryAsync(_shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "QuickSTRM: Initial webhook sync encountered an error");
            }
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdownCts.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the base URL for Jellyfin that external *arr instances will use for webhooks.
    /// </summary>
    public string GetEffectiveJellyfinUrl()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is not null && !string.IsNullOrWhiteSpace(config.JellyfinBaseUrl))
        {
            return config.JellyfinBaseUrl.TrimEnd('/');
        }

        return "http://localhost:8096";
    }

    /// <summary>
    /// Performs an immediate, single-attempt webhook synchronization across all configured *arr instances.
    /// Used by configuration updates and the manual "Sync Webhooks Now" API endpoint to bypass background retries.
    /// </summary>
    public async Task<List<ArrSyncResult>> SyncWebhooksAsync(CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.ArrInstances.Count == 0)
        {
            return new List<ArrSyncResult>();
        }

        var results = new List<ArrSyncResult>();
        var webhookSecret = config.WebhookSecret;
        var webhookUrl = $"{GetEffectiveJellyfinUrl()}/QuickStrm/ArrWebhook?token={webhookSecret}";
        var modified = false;

        foreach (var instance in config.ArrInstances)
        {
            var result = await SyncSingleInstanceAsync(instance, webhookUrl, ct).ConfigureAwait(false);
            results.Add(result);
            if (result.Success)
            {
                modified = true;
            }
        }

        if (modified && Plugin.Instance is not null)
        {
            Plugin.Instance.SaveConfiguration();
        }

        return results;
    }

    /// <summary>
    /// Background startup sync with exponential backoff retry loop (maximum 5 attempts per instance).
    /// Protects against container race conditions where Jellyfin starts before Radarr/Sonarr.
    /// </summary>
    public async Task SyncWebhooksWithRetryAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.ArrInstances.Count == 0)
        {
            return;
        }

        var webhookSecret = config.WebhookSecret;
        var webhookUrl = $"{GetEffectiveJellyfinUrl()}/QuickStrm/ArrWebhook?token={webhookSecret}";
        var modified = false;

        foreach (var instance in config.ArrInstances)
        {
            if (string.IsNullOrWhiteSpace(instance.BaseUrl) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            const int maxAttempts = 5;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    var result = await SyncSingleInstanceAsync(instance, webhookUrl, ct).ConfigureAwait(false);
                    if (result.Success)
                    {
                        modified = true;
                        _logger.LogInformation("QuickSTRM: Webhook synchronized for {Name} on attempt {Attempt}/{Max}", instance.Name, attempt, maxAttempts);
                        break;
                    }

                    if (attempt < maxAttempts)
                    {
                        var delaySec = 10 * (int)Math.Pow(2, attempt - 1); // 10s, 20s, 40s, 80s
                        _logger.LogInformation(
                            "QuickSTRM: Webhook sync attempt {Attempt}/{Max} failed for {Name} ({Message}). Retrying in {Seconds}s...",
                            attempt, maxAttempts, instance.Name, result.Message, delaySec);
                        await Task.Delay(TimeSpan.FromSeconds(delaySec), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "QuickSTRM: Failed to connect to {Name} at {BaseUrl} after {Max} attempts ({Message}). Instance is unreachable; skipping auto-sync.",
                            instance.Name, instance.BaseUrl, maxAttempts, result.Message);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt < maxAttempts)
                    {
                        var delaySec = 10 * (int)Math.Pow(2, attempt - 1);
                        _logger.LogInformation(
                            ex,
                            "QuickSTRM: Webhook sync attempt {Attempt}/{Max} encountered error for {Name}. Retrying in {Seconds}s...",
                            attempt, maxAttempts, instance.Name, delaySec);
                        await Task.Delay(TimeSpan.FromSeconds(delaySec), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogWarning(
                            ex,
                            "QuickSTRM: Failed to connect to {Name} at {BaseUrl} after {Max} attempts. Instance is unreachable; skipping auto-sync.",
                            instance.Name, instance.BaseUrl, maxAttempts);
                    }
                }
            }
        }

        if (modified && Plugin.Instance is not null)
        {
            Plugin.Instance.SaveConfiguration();
        }
    }

    /// <summary>
    /// Performs an immediate, single-attempt webhook synchronization for a single *arr instance.
    /// </summary>
    public async Task<ArrSyncResult> SyncSingleInstanceAsync(ArrInstanceConfig instance, string webhookUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.BaseUrl) || string.IsNullOrWhiteSpace(instance.ApiKey))
        {
            return new ArrSyncResult(instance.Name, false, "BaseUrl or ApiKey missing", null);
        }

        var baseUrl = instance.BaseUrl.TrimEnd('/');
        try
        {
            // 1. Fetch existing notifications
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/notification");
            getRequest.Headers.Add("X-Api-Key", instance.ApiKey);

            using var getResponse = await _httpClient.SendAsync(getRequest, ct).ConfigureAwait(false);
            if (!getResponse.IsSuccessStatusCode)
            {
                return new ArrSyncResult(instance.Name, false, $"HTTP {getResponse.StatusCode} retrieving notifications", null);
            }

            var jsonStr = await getResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(jsonStr);

            int? existingId = null;
            string? existingUrl = null;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("name", out var nameProp)
                    && string.Equals(nameProp.GetString(), WebhookNotificationName, StringComparison.OrdinalIgnoreCase))
                {
                    if (element.TryGetProperty("id", out var idProp))
                    {
                        existingId = idProp.GetInt32();
                        if (element.TryGetProperty("fields", out var fieldsProp) && fieldsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var field in fieldsProp.EnumerateArray())
                            {
                                if (field.TryGetProperty("name", out var fn) && string.Equals(fn.GetString(), "url", StringComparison.OrdinalIgnoreCase))
                                {
                                    existingUrl = field.TryGetProperty("value", out var fv) ? fv.GetString() : null;
                                    break;
                                }
                            }
                        }
                        break;
                    }
                }
            }

            if (existingId.HasValue && string.Equals(existingUrl, webhookUrl, StringComparison.OrdinalIgnoreCase))
            {
                instance.ProvisionedWebhookId = existingId.Value;
                return new ArrSyncResult(instance.Name, true, "Webhook already provisioned with security token", existingId.Value);
            }

            if (existingId.HasValue)
            {
                // Existing webhook is missing token or has an outdated URL; remove it before provisioning the update
                using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/v3/notification/{existingId.Value}");
                delReq.Headers.Add("X-Api-Key", instance.ApiKey);
                await _httpClient.SendAsync(delReq, ct).ConfigureAwait(false);
                _logger.LogInformation("QuickSTRM: Cleaned up outdated webhook #{Id} on {Name} to upgrade with security token", existingId.Value, instance.Name);
            }

            // 2. Provision new webhook notification
            var payload = new
            {
                name = WebhookNotificationName,
                implementation = "Webhook",
                configContract = "WebhookSettings",
                onGrab = false,
                onDownload = true,
                onUpgrade = true,
                onRename = true,
                onMovieFileDelete = true,
                onEpisodeFileDelete = true,
                fields = new object[]
                {
                    new { name = "url", value = webhookUrl },
                    new { name = "method", value = 1 } // POST
                }
            };

            using var postRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v3/notification");
            postRequest.Headers.Add("X-Api-Key", instance.ApiKey);
            postRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var postResponse = await _httpClient.SendAsync(postRequest, ct).ConfigureAwait(false);
            if (!postResponse.IsSuccessStatusCode)
            {
                var err = await postResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new ArrSyncResult(instance.Name, false, $"HTTP {postResponse.StatusCode}: {err}", null);
            }

            var postJson = await postResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var createdDoc = JsonDocument.Parse(postJson);
            if (createdDoc.RootElement.TryGetProperty("id", out var newIdProp))
            {
                var newId = newIdProp.GetInt32();
                instance.ProvisionedWebhookId = newId;
                _logger.LogInformation("QuickSTRM: Successfully auto-provisioned secured webhook #{Id} in {Name}", newId, instance.Name);
                return new ArrSyncResult(instance.Name, true, "Secured webhook provisioned successfully", newId);
            }

            return new ArrSyncResult(instance.Name, true, "Secured webhook created (no ID returned)", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Failed to sync webhook for {Name} at {BaseUrl}", instance.Name, baseUrl);
            return new ArrSyncResult(instance.Name, false, ex.Message, null);
        }
    }

    /// <summary>
    /// Cleanly uninstalls and deletes all provisioned QuickSTRM webhooks across all connected *arr instances.
    /// </summary>
    public async Task<List<ArrSyncResult>> RemoveAllWebhooksAsync(CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.ArrInstances.Count == 0)
        {
            return new List<ArrSyncResult>();
        }

        var results = new List<ArrSyncResult>();
        var modified = false;

        foreach (var instance in config.ArrInstances)
        {
            if (string.IsNullOrWhiteSpace(instance.BaseUrl) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            var baseUrl = instance.BaseUrl.TrimEnd('/');
            try
            {
                // First check if a notification named "QuickSTRM Auto-Sync" exists to be 100% sure we clean it up
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/notification");
                getRequest.Headers.Add("X-Api-Key", instance.ApiKey);

                using var getResponse = await _httpClient.SendAsync(getRequest, ct).ConfigureAwait(false);
                if (getResponse.IsSuccessStatusCode)
                {
                    var jsonStr = await getResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(jsonStr);

                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        if (element.TryGetProperty("name", out var nameProp)
                            && string.Equals(nameProp.GetString(), WebhookNotificationName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (element.TryGetProperty("id", out var idProp))
                            {
                                var id = idProp.GetInt32();
                                using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/v3/notification/{id}");
                                deleteRequest.Headers.Add("X-Api-Key", instance.ApiKey);
                                await _httpClient.SendAsync(deleteRequest, ct).ConfigureAwait(false);
                                _logger.LogInformation("QuickSTRM: Cleanly removed webhook #{Id} from {Name}", id, instance.Name);
                            }
                        }
                    }
                }

                instance.ProvisionedWebhookId = null;
                modified = true;
                results.Add(new ArrSyncResult(instance.Name, true, "Cleaned up all webhooks", null));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "QuickSTRM: Error removing webhook from {Name}", instance.Name);
                results.Add(new ArrSyncResult(instance.Name, false, ex.Message, null));
            }
        }

        if (modified && Plugin.Instance is not null)
        {
            Plugin.Instance.SaveConfiguration();
        }

        return results;
    }

    /// <summary>
    /// Performs a live connectivity check against an *arr instance without persisting configuration or provisioning webhooks.
    /// Sends a GET request to {config.BaseUrl}/api/v3/system/status using the provided API key in the X-Api-Key header.
    /// </summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync(ArrInstanceConfig config, CancellationToken ct = default)
    {
        if (config is null || string.IsNullOrWhiteSpace(config.BaseUrl) || string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return (false, "Base URL and API Key are required.");
        }

        var baseUrl = config.BaseUrl.Trim().TrimEnd('/');
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/system/status");
            request.Headers.Add("X-Api-Key", config.ApiKey.Trim());

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
                    var appName = root.TryGetProperty("appName", out var an) ? an.GetString()
                        : root.TryGetProperty("instanceName", out var iname) ? iname.GetString()
                        : null;

                    var name = !string.IsNullOrWhiteSpace(appName) ? appName : config.ArrType.ToString();
                    var verStr = !string.IsNullOrWhiteSpace(version) ? $" (v{version})" : string.Empty;
                    return (true, $"Connected to {name}{verStr} successfully.");
                }
                catch
                {
                    return (true, "Connection successful.");
                }
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return (false, $"Authentication failed (HTTP {(int)response.StatusCode}). Please verify your API Key.");
            }

            return (false, $"Server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Connection test to {Url} failed", baseUrl);
            return (false, $"Connection failed: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "QuickSTRM: Connection test to {Url} timed out", baseUrl);
            return (false, "Connection timed out. Please verify that the Base URL is reachable.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Connection test was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Error during connection test to {Url}", baseUrl);
            return (false, $"Connection error: {ex.Message}");
        }
    }

    /// <summary>
    /// Translates an incoming file path from an *arr webhook payload to a local Jellyfin filesystem path.
    /// </summary>
    public string? TranslateArrPathToLocal(string incomingPath, out ArrInstanceConfig? matchedInstance)
    {
        matchedInstance = null;
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(incomingPath))
        {
            return null;
        }

        // Normalize incoming slashes for robust prefix matching
        var normalizedIncoming = incomingPath.Replace('\\', '/');

        foreach (var instance in config.ArrInstances)
        {
            if (string.IsNullOrWhiteSpace(instance.PathFrom) || string.IsNullOrWhiteSpace(instance.PathTo))
            {
                continue;
            }

            var normalizedFrom = instance.PathFrom.Replace('\\', '/').TrimEnd('/');
            if (normalizedIncoming.StartsWith(normalizedFrom, StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalizedIncoming.Substring(normalizedFrom.Length).TrimStart('/');
                var local = Path.Combine(instance.PathTo, relative.Replace('/', Path.DirectorySeparatorChar));
                matchedInstance = instance;
                return local;
            }
        }

        return null;
    }

    /// <summary>
    /// Translates a local Jellyfin path back to the path scheme of the matching *arr instance.
    /// </summary>
    public string? TranslateLocalPathToArr(string localPath, out ArrInstanceConfig? matchedInstance)
    {
        matchedInstance = null;
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(localPath))
        {
            return null;
        }

        var normalizedLocal = localPath.Replace('\\', '/');

        foreach (var instance in config.ArrInstances)
        {
            if (string.IsNullOrWhiteSpace(instance.PathTo) || string.IsNullOrWhiteSpace(instance.PathFrom))
            {
                continue;
            }

            var normalizedTo = instance.PathTo.Replace('\\', '/').TrimEnd('/');
            if (normalizedLocal.StartsWith(normalizedTo, StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalizedLocal.Substring(normalizedTo.Length).TrimStart('/');
                var arrPath = instance.PathFrom.TrimEnd('/', '\\') + "/" + relative;
                matchedInstance = instance;
                return arrPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Invoked when a .strm is deleted due to confirmed HTTP 404 (permanent failure).
    /// Deletes the corresponding file record in the matching *arr instance and triggers an automatic search command.
    /// </summary>
    public async Task TriggerArrHealAsync(string localStrmPath, CancellationToken ct = default)
    {
        var arrPath = TranslateLocalPathToArr(localStrmPath, out var instance);
        if (arrPath is null || instance is null || !instance.AutoHealOn404)
        {
            return;
        }

        var baseUrl = instance.BaseUrl.TrimEnd('/');
        _logger.LogInformation("QuickSTRM Auto-Heal: Detected 404 dead link at {LocalPath}; initiating recovery on {Name} for {ArrPath}", localStrmPath, instance.Name, arrPath);

        try
        {
            if (instance.ArrType is ArrType.Radarr or ArrType.Whisparr)
            {
                await HealRadarrMovieAsync(baseUrl, instance.ApiKey, arrPath, ct).ConfigureAwait(false);
            }
            else if (instance.ArrType == ArrType.Sonarr)
            {
                await HealSonarrEpisodeAsync(baseUrl, instance.ApiKey, arrPath, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM Auto-Heal: Recovery attempt failed for {Path} on {Name}", arrPath, instance.Name);
        }
    }

    private async Task HealRadarrMovieAsync(string baseUrl, string apiKey, string arrPath, CancellationToken ct)
    {
        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/movie");
        getReq.Headers.Add("X-Api-Key", apiKey);

        using var getResp = await _httpClient.SendAsync(getReq, ct).ConfigureAwait(false);
        if (!getResp.IsSuccessStatusCode)
        {
            return;
        }

        var json = await getResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var normArrPath = arrPath.Replace('\\', '/');

        foreach (var movie in doc.RootElement.EnumerateArray())
        {
            var movieId = movie.GetProperty("id").GetInt32();
            int? movieFileId = null;

            if (movie.TryGetProperty("movieFile", out var mf) && mf.ValueKind == JsonValueKind.Object)
            {
                if (mf.TryGetProperty("path", out var p) && string.Equals(p.GetString()?.Replace('\\', '/'), normArrPath, StringComparison.OrdinalIgnoreCase))
                {
                    movieFileId = mf.GetProperty("id").GetInt32();
                }
            }

            if (!movieFileId.HasValue && movie.TryGetProperty("path", out var movieDir))
            {
                var dir = movieDir.GetString()?.Replace('\\', '/');
                if (dir is not null && normArrPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                {
                    if (movie.TryGetProperty("movieFileId", out var mfid) && mfid.GetInt32() > 0)
                    {
                        movieFileId = mfid.GetInt32();
                    }
                }
            }

            if (movieFileId.HasValue)
            {
                // Delete the dead moviefile from Radarr
                using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/v3/moviefile/{movieFileId.Value}");
                delReq.Headers.Add("X-Api-Key", apiKey);
                await _httpClient.SendAsync(delReq, ct).ConfigureAwait(false);

                // Command Radarr to search for a new release
                var cmd = new { name = "MoviesSearch", movieIds = new[] { movieId } };
                using var cmdReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v3/command");
                cmdReq.Headers.Add("X-Api-Key", apiKey);
                cmdReq.Content = new StringContent(JsonSerializer.Serialize(cmd), Encoding.UTF8, "application/json");
                await _httpClient.SendAsync(cmdReq, ct).ConfigureAwait(false);

                _logger.LogInformation("QuickSTRM Auto-Heal: Removed dead moviefile #{FileId} and triggered MoviesSearch for movie #{MovieId}", movieFileId.Value, movieId);
                return;
            }
        }
    }

    private async Task HealSonarrEpisodeAsync(string baseUrl, string apiKey, string arrPath, CancellationToken ct)
    {
        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/series");
        getReq.Headers.Add("X-Api-Key", apiKey);

        using var getResp = await _httpClient.SendAsync(getReq, ct).ConfigureAwait(false);
        if (!getResp.IsSuccessStatusCode)
        {
            return;
        }

        var json = await getResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var normArrPath = arrPath.Replace('\\', '/');

        foreach (var series in doc.RootElement.EnumerateArray())
        {
            if (!series.TryGetProperty("path", out var seriesPathProp))
            {
                continue;
            }

            var sPath = seriesPathProp.GetString()?.Replace('\\', '/');
            if (sPath is not null && normArrPath.StartsWith(sPath, StringComparison.OrdinalIgnoreCase))
            {
                var seriesId = series.GetProperty("id").GetInt32();

                // Query episode files for this series
                using var efReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/episodefile?seriesId={seriesId}");
                efReq.Headers.Add("X-Api-Key", apiKey);

                using var efResp = await _httpClient.SendAsync(efReq, ct).ConfigureAwait(false);
                if (efResp.IsSuccessStatusCode)
                {
                    var efJson = await efResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var efDoc = JsonDocument.Parse(efJson);

                    foreach (var ef in efDoc.RootElement.EnumerateArray())
                    {
                        if (ef.TryGetProperty("path", out var efPath)
                            && string.Equals(efPath.GetString()?.Replace('\\', '/'), normArrPath, StringComparison.OrdinalIgnoreCase))
                        {
                            var efId = ef.GetProperty("id").GetInt32();

                            // Query episode ID(s) associated with this episode file before deleting
                            var episodeIds = new List<int>();
                            try
                            {
                                using var epReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/episode?seriesId={seriesId}&episodeFileId={efId}");
                                epReq.Headers.Add("X-Api-Key", apiKey);
                                using var epResp = await _httpClient.SendAsync(epReq, ct).ConfigureAwait(false);
                                if (epResp.IsSuccessStatusCode)
                                {
                                    var epJson = await epResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                                    using var epDoc = JsonDocument.Parse(epJson);
                                    foreach (var ep in epDoc.RootElement.EnumerateArray())
                                    {
                                        if (ep.TryGetProperty("id", out var epIdProp))
                                        {
                                            episodeIds.Add(epIdProp.GetInt32());
                                        }
                                    }
                                }
                            }
                            catch
                            {
                            }

                            // Delete episode file
                            using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/api/v3/episodefile/{efId}");
                            delReq.Headers.Add("X-Api-Key", apiKey);
                            await _httpClient.SendAsync(delReq, ct).ConfigureAwait(false);

                            // Trigger EpisodeSearch command if episode IDs found, otherwise fallback to SeriesSearch
                            object cmd = episodeIds.Count > 0
                                ? new { name = "EpisodeSearch", episodeIds = episodeIds.ToArray() }
                                : new { name = "SeriesSearch", seriesId };

                            using var cmdReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v3/command");
                            cmdReq.Headers.Add("X-Api-Key", apiKey);
                            cmdReq.Content = new StringContent(JsonSerializer.Serialize(cmd), Encoding.UTF8, "application/json");
                            await _httpClient.SendAsync(cmdReq, ct).ConfigureAwait(false);

                            _logger.LogInformation("QuickSTRM Auto-Heal: Removed dead episodefile #{FileId} and triggered replacement search for series #{SeriesId}", efId, seriesId);
                            return;
                        }
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _httpClient.Dispose();
        _shutdownCts.Dispose();
    }
}

/// <summary>
/// Status result returned when synchronizing webhooks with an *arr instance.
/// </summary>
public record ArrSyncResult(string InstanceName, bool Success, string Message, int? WebhookId);
