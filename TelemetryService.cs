using System;
using System.Threading.Tasks;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;
using QuickSTRM.Configuration;

namespace QuickSTRM;

/// <summary>
/// Telemetry service leveraging native Jellyfin activity logging and in-memory action ledger.
/// </summary>
public class TelemetryService
{
    private readonly IActivityManager _activityManager;
    private readonly ILogger<TelemetryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryService"/> class.
    /// </summary>
    /// <param name="activityManager">Jellyfin's native activity manager.</param>
    /// <param name="logger">Logger instance.</param>
    public TelemetryService(
        IActivityManager activityManager,
        ILogger<TelemetryService> logger)
    {
        _activityManager = activityManager;
        _logger = logger;
    }

    /// <summary>
    /// Logs an event to Jellyfin's native activity manager and the plugin's action ledger.
    /// </summary>
    /// <param name="name">Descriptive name of the event.</param>
    /// <param name="overview">Detailed overview or description.</param>
    /// <param name="severity">Log severity level.</param>
    public async Task LogEventAsync(string name, string overview, LogLevel severity = LogLevel.Information)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is not null)
            {
                config.AddLedgerEntry($"[{name}] {overview}");
                Plugin.Instance?.SaveConfiguration();
            }

            var entry = new ActivityLogEntry(name, "QuickSTRM", Guid.Empty)
            {
                Overview = overview,
                ShortOverview = overview,
                Date = DateTime.UtcNow,
                Severity = severity
            };

            await _activityManager.CreateAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Failed to publish activity log entry: {Name} - {Overview}", name, overview);
        }
    }

    /// <summary>
    /// Logs an STRM auto-heal event when a dead .strm file is purged.
    /// </summary>
    /// <param name="purgedPath">The path of the purged .strm file.</param>
    /// <param name="itemName">Optional item name.</param>
    public Task LogAutoHealAsync(string purgedPath, string? itemName = null)
    {
        var overview = string.IsNullOrEmpty(itemName)
            ? purgedPath
            : $"{itemName} ({purgedPath})";
        return LogEventAsync("STRM Auto-Heal", overview, LogLevel.Warning);
    }

    /// <summary>
    /// Logs a circuit breaker state transition event (e.g. Tripped, Reset).
    /// </summary>
    /// <param name="action">Action name such as "Tripped" or "Reset".</param>
    /// <param name="details">Details explaining the condition or recovery timeline.</param>
    public Task LogCircuitBreakerAsync(string action, string details)
    {
        var name = $"Circuit Breaker {action}";
        var severity = string.Equals(action, "Tripped", StringComparison.OrdinalIgnoreCase)
            ? LogLevel.Error
            : LogLevel.Information;
        return LogEventAsync(name, details, severity);
    }
}
