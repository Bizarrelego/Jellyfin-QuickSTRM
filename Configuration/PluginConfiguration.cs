using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace QuickSTRM.Configuration;

/// <summary>
/// Supported *arr application types.
/// </summary>
public enum ArrType
{
    Radarr,
    Sonarr,
    Whisparr
}

/// <summary>
/// Configuration for a connected *arr instance.
/// </summary>
public class ArrInstanceConfig
{
    /// <summary>Gets or sets a friendly display name for this instance.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the type of *arr instance (Radarr, Sonarr, Whisparr).</summary>
    public ArrType ArrType { get; set; } = ArrType.Radarr;

    /// <summary>Gets or sets the base URL for the *arr instance (e.g. http://192.168.1.100:7878).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the API key for authenticating with the *arr REST API.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the root path as reported by the *arr instance (e.g. /movies).</summary>
    public string PathFrom { get; set; } = string.Empty;

    /// <summary>Gets or sets the corresponding local path as mapped in Jellyfin (e.g. /mnt/cloud/movies).</summary>
    public string PathTo { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether to delete the missing record in *arr and trigger a search on confirmed HTTP 404.</summary>
    public bool AutoHealOn404 { get; set; }

    /// <summary>Gets or sets the webhook ID provisioned in the *arr instance, or null if not yet provisioned.</summary>
    public int? ProvisionedWebhookId { get; set; }
}

/// <summary>
/// Plugin configuration model for QuickSTRM.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether to automatically probe
    /// newly added .strm items that lack stream info.
    /// </summary>
    public bool EnableAutoProbe { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether dead .strm files (failed probe/HEAD) are deleted from disk
    /// and removed from the library. Disabled while the circuit breaker is tripped.
    /// </summary>
    public bool EnableAutoDelete { get; set; }

    /// <summary>
    /// Gets or sets the optional name of a marker file that exists on the cloud mount (searched up to 5 directory levels above
    /// a .strm). If set and not found, a missing .strm is treated as a dropped mount, never as a deletion. Default: empty.
    /// </summary>
    public string MountMarkerFileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of connected *arr instances for zero-touch webhook synchronization and auto-healing.
    /// </summary>
    public List<ArrInstanceConfig> ArrInstances { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether playing an episode automatically pre-warms the next episode in the background.
    /// </summary>
    public bool EnableNextEpisodePreWarm { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to synthesize 5-minute virtual chapters on .strm items once runtime is known.
    /// </summary>
    public bool SynthesizeVirtualChapters { get; set; } = true;

    /// <summary>
    /// Gets or sets the Jellyfin Base URL reachable by external *arr instances (e.g. http://192.168.1.100:8096).
    /// If empty, the local API server URL is used automatically.
    /// </summary>
    public string JellyfinBaseUrl { get; set; } = string.Empty;

    private string _webhookSecret = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets the auto-generated secret token required to authorize incoming *arr webhooks.
    /// </summary>
    public string WebhookSecret
    {
        get => string.IsNullOrEmpty(_webhookSecret) ? (_webhookSecret = Guid.NewGuid().ToString("N")) : _webhookSecret;
        set => _webhookSecret = string.IsNullOrEmpty(value) ? Guid.NewGuid().ToString("N") : value;
    }

    /// <summary>
    /// In-memory queue of recent action ledger entries capped at 100 entries.
    /// </summary>
    [System.Xml.Serialization.XmlIgnore]
    public Queue<string> ActionLedger { get; set; } = new();

    /// <summary>
    /// XML-serializable surrogate for ActionLedger to ensure safe serialization without XmlSerializer reflection errors.
    /// </summary>
    [System.Xml.Serialization.XmlElement("ActionLedger")]
    public string[] ActionLedgerSerialized
    {
        get
        {
            lock (_ledgerLock)
            {
                return ActionLedger.ToArray();
            }
        }
        set
        {
            lock (_ledgerLock)
            {
                ActionLedger = value == null ? new Queue<string>() : new Queue<string>(value);
            }
        }
    }

    private readonly object _ledgerLock = new();

    /// <summary>
    /// Appends a formatted timestamped string to the ActionLedger, capping at 100 entries.
    /// </summary>
    public void AddLedgerEntry(string message)
    {
        lock (_ledgerLock)
        {
            while (ActionLedger.Count >= 100)
            {
                ActionLedger.Dequeue();
            }
            ActionLedger.Enqueue($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss UTC}] {message}");
        }
    }
}

