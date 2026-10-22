using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace QuickSTRM.Configuration;

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
    /// Gets or sets a value indicating whether proactive 3:00 AM sweep is enabled.
    /// Alias for EnableAutoProbe.
    /// </summary>
    [System.Xml.Serialization.XmlIgnore]
    public bool EnableProactiveSweeper
    {
        get => EnableAutoProbe;
        set => EnableAutoProbe = value;
    }

    /// <summary>
    /// Alias for EnableAutoProbe.
    /// </summary>
    [System.Xml.Serialization.XmlIgnore]
    public bool EnableSweeper
    {
        get => EnableAutoProbe;
        set => EnableAutoProbe = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether dead .strm files (failed probe/HEAD) are deleted from disk
    /// and queued into the native auto-heal feed. Disabled while the circuit breaker is tripped.
    /// </summary>
    public bool EnableAutoDelete { get; set; } = true;

    /// <summary>
    /// Alias for EnableAutoDelete.
    /// </summary>
    [System.Xml.Serialization.XmlIgnore]
    public bool EnableAutoHeal
    {
        get => EnableAutoDelete;
        set => EnableAutoDelete = value;
    }

    /// <summary>
    /// Operational mode for manual library pre-probing.
    /// </summary>
    public ProbeMode ProbeMode { get; set; } = ProbeMode.OnlyUnprobed;

    /// <summary>
    /// Gets or sets the optional name of a marker file that exists on the cloud mount (searched up to 5 directory levels above
    /// a .strm). If set and not found, a missing .strm is treated as a dropped mount, never as a deletion. Default: empty.
    /// </summary>
    public string MountMarkerFileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether playing an episode automatically pre-warms the next episode in the background.
    /// </summary>
    public bool EnableNextEpisodePreWarm { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to synthesize 5-minute virtual chapters on .strm items once runtime is known.
    /// </summary>
    public bool SynthesizeVirtualChapters { get; set; } = true;

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

    /// <summary>
    /// Empties the action ledger history.
    /// </summary>
    public void ClearLedger()
    {
        lock (_ledgerLock)
        {
            ActionLedger.Clear();
        }
    }
}
