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
    /// Gets or sets a value indicating whether dead .strm files (failed probe/HEAD) are deleted from disk
    /// and removed from the library. Disabled while the circuit breaker is tripped.
    /// </summary>
    public bool EnableAutoDelete { get; set; }

    /// <summary>
    /// Gets or sets the optional name of a marker file that exists on the cloud mount (searched up to 5 directory levels above
    /// a .strm). If set and not found, a missing .strm is treated as a dropped mount, never as a deletion. Default: empty.
    /// </summary>
    public string MountMarkerFileName { get; set; } = string.Empty;
}
