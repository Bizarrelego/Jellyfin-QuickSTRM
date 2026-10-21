using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Sockets;
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

        foreach (var instance in config.ArrInstances)
        {
            var result = await SyncSingleInstanceAsync(instance, webhookUrl, ct).ConfigureAwait(false);
            results.Add(result);
        }

        if (Plugin.Instance is not null)
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
    }

    /// <summary>
    /// Performs an immediate, single-attempt webhook synchronization for a single *arr instance.
    /// Uses schema-driven template querying to eliminate foreign property validator crashes.
    /// </summary>
    public async Task<ArrSyncResult> SyncSingleInstanceAsync(ArrInstanceConfig config, string webhookUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.BaseUrl) || string.IsNullOrWhiteSpace(config.ApiKey))
        {
            var missingMsg = "Base URL or API Key is missing.";
            config.ProvisionedWebhookId = null;
            config.LastSyncError = missingMsg;
            Plugin.Instance?.SaveConfiguration();
            return new ArrSyncResult(config.Name, false, missingMsg, null);
        }

        var cleanBaseUrl = config.BaseUrl.TrimEnd('/');
        try
        {
            // 1. Fetch existing notifications
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{cleanBaseUrl}/api/v3/notification");
            getRequest.Headers.Add("X-Api-Key", config.ApiKey.Trim());
            getRequest.Headers.Add("Accept", "application/json");

            using var getResponse = await _httpClient.SendAsync(getRequest, ct).ConfigureAwait(false);
            if (!getResponse.IsSuccessStatusCode)
            {
                var errBody = await getResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var friendly = ParseArrErrorMessage(getResponse, errBody);
                config.ProvisionedWebhookId = null;
                config.LastSyncError = friendly;
                Plugin.Instance?.SaveConfiguration();
                return new ArrSyncResult(config.Name, false, friendly, null);
            }

            var jsonStr = await getResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(jsonStr);

            int? existingId = null;
            JsonObject? existingObj = null;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("name", out var nameProp)
                    && string.Equals(nameProp.GetString(), WebhookNotificationName, StringComparison.OrdinalIgnoreCase))
                {
                    if (element.TryGetProperty("id", out var idProp))
                    {
                        existingId = idProp.GetInt32();
                        try
                        {
                            existingObj = JsonNode.Parse(element.GetRawText()) as JsonObject;
                        }
                        catch
                        {
                        }
                        break;
                    }
                }
            }

            // 2. If existing webhook exists, perform schema-safe mutation and PUT /api/v3/notification/{id}
            if (existingId.HasValue)
            {
                JsonObject? targetObj = null;
                try
                {
                    using var getSingleReq = new HttpRequestMessage(HttpMethod.Get, $"{cleanBaseUrl}/api/v3/notification/{existingId.Value}");
                    getSingleReq.Headers.Add("X-Api-Key", config.ApiKey.Trim());
                    getSingleReq.Headers.Add("Accept", "application/json");
                    using var getSingleResp = await _httpClient.SendAsync(getSingleReq, ct).ConfigureAwait(false);
                    if (getSingleResp.IsSuccessStatusCode)
                    {
                        var singleJson = await getSingleResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        targetObj = JsonNode.Parse(singleJson) as JsonObject;
                    }
                }
                catch
                {
                }

                targetObj ??= existingObj;
                if (targetObj is not null)
                {
                    targetObj["id"] = existingId.Value;
                    ApplyArrProperties(targetObj, config.ArrType);
                    UpdateArrFields(targetObj, webhookUrl);

                    using var putReq = new HttpRequestMessage(HttpMethod.Put, $"{cleanBaseUrl}/api/v3/notification/{existingId.Value}");
                    putReq.Headers.Add("X-Api-Key", config.ApiKey.Trim());
                    putReq.Headers.Add("Accept", "application/json");
                    putReq.Content = new StringContent(targetObj.ToJsonString(), Encoding.UTF8, "application/json");

                    using var putResp = await _httpClient.SendAsync(putReq, ct).ConfigureAwait(false);
                    if (!putResp.IsSuccessStatusCode)
                    {
                        var putErr = await putResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        var friendly = ParseArrErrorMessage(putResp, putErr);
                        config.ProvisionedWebhookId = null;
                        config.LastSyncError = friendly;
                        Plugin.Instance?.SaveConfiguration();
                        return new ArrSyncResult(config.Name, false, friendly, null);
                    }

                    config.ProvisionedWebhookId = existingId.Value;
                    config.LastSyncError = string.Empty;
                    config.LastSyncUtc = DateTime.UtcNow.ToString("o");
                    Plugin.Instance?.SaveConfiguration();

                    _logger.LogInformation("QuickSTRM: Updated and verified webhook #{Id} in {Name}", existingId.Value, config.Name);
                    return new ArrSyncResult(config.Name, true, "Secured webhook updated and verified successfully", existingId.Value);
                }
            }

            // 3. Provision new webhook notification using schema template to avoid foreign property validation crashes
            JsonObject? newPayload = null;

            try
            {
                using var schemaReq = new HttpRequestMessage(HttpMethod.Get, $"{cleanBaseUrl}/api/v3/notification/schema");
                schemaReq.Headers.Add("X-Api-Key", config.ApiKey.Trim());
                schemaReq.Headers.Add("Accept", "application/json");

                using var schemaResp = await _httpClient.SendAsync(schemaReq, ct).ConfigureAwait(false);
                if (schemaResp.IsSuccessStatusCode)
                {
                    var schemaJson = await schemaResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var schemaNode = JsonNode.Parse(schemaJson);
                    if (schemaNode is JsonArray schemaArr)
                    {
                        foreach (var item in schemaArr)
                        {
                            if (item is JsonObject obj && obj.TryGetPropertyValue("implementation", out var implVal)
                                && string.Equals(implVal?.ToString(), "Webhook", StringComparison.OrdinalIgnoreCase))
                            {
                                newPayload = JsonNode.Parse(obj.ToJsonString()) as JsonObject;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "QuickSTRM: Could not retrieve notification schema from {Name}, falling back to manual template", config.Name);
            }

            if (newPayload is null)
            {
                newPayload = new JsonObject
                {
                    ["name"] = WebhookNotificationName,
                    ["implementation"] = "Webhook",
                    ["configContract"] = "WebhookSettings",
                    ["fields"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "url", ["value"] = webhookUrl },
                        new JsonObject { ["name"] = "method", ["value"] = 1 }
                    }
                };
            }

            ApplyArrProperties(newPayload, config.ArrType);
            UpdateArrFields(newPayload, webhookUrl);

            using var postRequest = new HttpRequestMessage(HttpMethod.Post, $"{cleanBaseUrl}/api/v3/notification");
            postRequest.Headers.Add("X-Api-Key", config.ApiKey.Trim());
            postRequest.Headers.Add("Accept", "application/json");
            postRequest.Content = new StringContent(newPayload.ToJsonString(), Encoding.UTF8, "application/json");

            using var postResponse = await _httpClient.SendAsync(postRequest, ct).ConfigureAwait(false);
            if (!postResponse.IsSuccessStatusCode)
            {
                var err = await postResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var friendly = ParseArrErrorMessage(postResponse, err);
                config.ProvisionedWebhookId = null;
                config.LastSyncError = friendly;
                Plugin.Instance?.SaveConfiguration();
                return new ArrSyncResult(config.Name, false, friendly, null);
            }

            var postJson = await postResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var createdDoc = JsonDocument.Parse(postJson);
            int? newId = null;
            if (createdDoc.RootElement.TryGetProperty("id", out var newIdProp))
            {
                newId = newIdProp.GetInt32();
            }

            config.ProvisionedWebhookId = newId;
            config.LastSyncError = string.Empty;
            config.LastSyncUtc = DateTime.UtcNow.ToString("o");
            Plugin.Instance?.SaveConfiguration();

            _logger.LogInformation("QuickSTRM: Successfully auto-provisioned secured webhook #{Id} in {Name}", newId, config.Name);
            return new ArrSyncResult(config.Name, true, "Secured webhook provisioned successfully", newId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Failed to sync webhook for {Name} at {BaseUrl}", config.Name, cleanBaseUrl);
            var friendly = ex is HttpRequestException or SocketException
                ? $"Could not reach {cleanBaseUrl} — verify container network or port ({ex.Message})"
                : ex.Message;
            config.ProvisionedWebhookId = null;
            config.LastSyncError = friendly;
            Plugin.Instance?.SaveConfiguration();
            return new ArrSyncResult(config.Name, false, friendly, null);
        }
    }

    /// <summary>
    /// Applies event booleans to a webhook notification payload, explicitly stripping foreign properties
    /// to avoid Radarr/Sonarr strict validator exceptions (e.g. ControllerActionInvoker 500).
    /// </summary>
    private static void ApplyArrProperties(JsonObject obj, ArrType arrType)
    {
        obj["name"] = WebhookNotificationName;
        obj["onGrab"] = false;
        obj["onDownload"] = true;
        obj["onUpgrade"] = true;
        obj["onRename"] = true;
        obj["onHealthIssue"] = false;

        if (arrType == ArrType.Sonarr)
        {
            obj["onSeriesDelete"] = true;
            obj["onEpisodeFileDelete"] = true;
            obj.Remove("onMovieDelete");
            obj.Remove("onMovieFileDelete");
        }
        else
        {
            // Radarr and Whisparr
            obj["onMovieDelete"] = true;
            obj["onMovieFileDelete"] = true;
            obj.Remove("onSeriesDelete");
            obj.Remove("onEpisodeFileDelete");
        }
    }

    /// <summary>
    /// Mutates the 'url' and 'method' entries in a notification's fields array, preserving all other fields.
    /// </summary>
    private static void UpdateArrFields(JsonObject obj, string webhookUrl)
    {
        if (obj["fields"] is not JsonArray fieldsArray)
        {
            fieldsArray = new JsonArray();
            obj["fields"] = fieldsArray;
        }

        JsonObject? urlField = null;
        JsonObject? methodField = null;

        foreach (var node in fieldsArray)
        {
            if (node is JsonObject fieldObj && fieldObj.TryGetPropertyValue("name", out var nameVal) && nameVal != null)
            {
                var nameStr = nameVal.ToString();
                if (string.Equals(nameStr, "url", StringComparison.OrdinalIgnoreCase))
                {
                    urlField = fieldObj;
                }
                else if (string.Equals(nameStr, "method", StringComparison.OrdinalIgnoreCase))
                {
                    methodField = fieldObj;
                }
            }
        }

        if (urlField is not null)
        {
            urlField["value"] = webhookUrl;
        }
        else
        {
            fieldsArray.Add(new JsonObject { ["name"] = "url", ["value"] = webhookUrl });
        }

        if (methodField is not null)
        {
            methodField["value"] = 1; // POST
        }
        else
        {
            fieldsArray.Add(new JsonObject { ["name"] = "method", ["value"] = 1 });
        }
    }

    /// <summary>
    /// Parses JSON error responses from Radarr/Sonarr/Whisparr and strips out raw C# stack traces,
    /// returning a clean, concise, 1-sentence diagnostic explanation.
    /// </summary>
    public static string ParseArrErrorMessage(HttpResponseMessage response, string rawBody)
    {
        var statusPrefix = $"HTTP {(int)response.StatusCode}";
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return !string.IsNullOrWhiteSpace(response.ReasonPhrase)
                ? $"{statusPrefix}: {response.ReasonPhrase}"
                : statusPrefix;
        }

        string? extracted = null;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                var errList = new List<string>();
                foreach (var item in root.EnumerateArray())
                {
                    if (item.TryGetProperty("errorMessage", out var em) && !string.IsNullOrWhiteSpace(em.GetString()))
                    {
                        var prop = item.TryGetProperty("propertyName", out var pn) ? pn.GetString() : null;
                        errList.Add(!string.IsNullOrWhiteSpace(prop) ? $"{prop}: {em.GetString()}" : em.GetString()!);
                    }
                    else if (item.TryGetProperty("message", out var m) && !string.IsNullOrWhiteSpace(m.GetString()))
                    {
                        errList.Add(m.GetString()!);
                    }
                }
                if (errList.Count > 0)
                {
                    extracted = string.Join("; ", errList);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("errorMessage", out var em) && !string.IsNullOrWhiteSpace(em.GetString()))
                {
                    extracted = em.GetString();
                }
                else if (root.TryGetProperty("message", out var m) && !string.IsNullOrWhiteSpace(m.GetString()))
                {
                    extracted = m.GetString();
                }
                else if (root.TryGetProperty("errors", out var errs))
                {
                    if (errs.ValueKind == JsonValueKind.Object)
                    {
                        var errList = new List<string>();
                        foreach (var prop in errs.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in prop.Value.EnumerateArray())
                                {
                                    errList.Add($"{prop.Name}: {item.GetString()}");
                                }
                            }
                            else
                            {
                                errList.Add($"{prop.Name}: {prop.Value.GetString()}");
                            }
                        }
                        if (errList.Count > 0)
                        {
                            extracted = string.Join("; ", errList);
                        }
                    }
                }
                else if (root.TryGetProperty("title", out var title) && !string.IsNullOrWhiteSpace(title.GetString()))
                {
                    extracted = title.GetString();
                }
            }
        }
        catch
        {
            // Non-JSON content
        }

        var text = extracted ?? rawBody;

        // Strip C# stack traces (at Microsoft.AspNetCore..., --- End of stack trace..., etc.)
        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var cleanLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("at ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("--- End of", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("NzbDrone.", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("ControllerActionInvoker", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            cleanLines.Add(trimmed);
        }

        var cleanMessage = cleanLines.Count > 0 ? string.Join(" ", cleanLines) : response.ReasonPhrase ?? "Unknown error";
        if (cleanMessage.Contains("   at "))
        {
            cleanMessage = cleanMessage.Substring(0, cleanMessage.IndexOf("   at ", StringComparison.Ordinal)).Trim();
        }

        if (cleanMessage.Length > 250)
        {
            cleanMessage = cleanMessage.Substring(0, 247) + "...";
        }

        return $"{statusPrefix}: {cleanMessage}";
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

            var cleanBaseUrl = instance.BaseUrl.TrimEnd('/');
            try
            {
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"{cleanBaseUrl}/api/v3/notification");
                getRequest.Headers.Add("X-Api-Key", instance.ApiKey.Trim());
                getRequest.Headers.Add("Accept", "application/json");

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
                                using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"{cleanBaseUrl}/api/v3/notification/{id}");
                                deleteRequest.Headers.Add("X-Api-Key", instance.ApiKey.Trim());
                                deleteRequest.Headers.Add("Accept", "application/json");
                                await _httpClient.SendAsync(deleteRequest, ct).ConfigureAwait(false);
                                _logger.LogInformation("QuickSTRM: Cleanly removed webhook #{Id} from {Name}", id, instance.Name);
                            }
                        }
                    }
                }

                instance.ProvisionedWebhookId = null;
                instance.LastSyncError = string.Empty;
                instance.LastSyncUtc = string.Empty;
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

        var cleanBaseUrl = config.BaseUrl.TrimEnd('/');
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{cleanBaseUrl}/api/v3/system/status");
            request.Headers.Add("X-Api-Key", config.ApiKey.Trim());
            request.Headers.Add("Accept", "application/json");

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

            var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var friendly = ParseArrErrorMessage(response, errBody);
            return (false, friendly);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Connection test to {Url} failed", cleanBaseUrl);
            return (false, $"Connection failed: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "QuickSTRM: Connection test to {Url} timed out", cleanBaseUrl);
            return (false, "Connection timed out. Please verify that the Base URL is reachable.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Connection test was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Error during connection test to {Url}", cleanBaseUrl);
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
