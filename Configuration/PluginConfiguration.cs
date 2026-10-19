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
}
