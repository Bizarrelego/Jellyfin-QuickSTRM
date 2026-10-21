using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;
using QuickSTRM.Configuration;

namespace QuickSTRM;

/// <summary>
/// Telemetry service leveraging native Jellyfin activity logging and in-memory action ledger.
/// </summary>
public class TelemetryService : IDisposable
{
    private readonly IActivityManager _activityManager;
    private readonly ILogger<TelemetryService> _logger;
    private readonly Timer _debounceSaveTimer;
    private bool _disposed;

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
        _debounceSaveTimer = new Timer(OnDebounceSaveElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    private void OnDebounceSaveElapsed(object? state)
    {
        try
        {
            Plugin.Instance?.SaveConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Debounced SaveConfiguration failed");
        }
    }

    private void TriggerDebouncedSave()
    {
        try
        {
            // Debounce for 3 seconds so rapid ledger entries batch into a single disk write
            _debounceSaveTimer.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Empties the action ledger and immediately persists the configuration.
    /// </summary>
    public void ClearLedger()
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is not null)
            {
                config.ClearLedger();
                Plugin.Instance?.SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuickSTRM: Failed to clear ledger");
        }
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
                TriggerDebouncedSave();
            }

            var entry = new ActivityLog(name, "QuickSTRM", Guid.Empty)
            {
                Overview = overview,
                ShortOverview = overview,
                DateCreated = DateTime.UtcNow,
                LogSeverity = severity
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounceSaveTimer.Dispose();

        try
        {
            Plugin.Instance?.SaveConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "QuickSTRM: Flush SaveConfiguration on shutdown failed");
        }
    }
}
