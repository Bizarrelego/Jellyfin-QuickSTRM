using System;
using System.Collections.Generic;
using System.Net.Http;
using QuickSTRM.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace QuickSTRM;

/// <summary>
/// QuickSTRM plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Specialized SocketsHttpHandler configured with aggressive connection pooling (15m),
    /// idle timeout (2m), and HTTP/2 multiplexing to eliminate TCP/TLS overhead.
    /// </summary>
    public static readonly SocketsHttpHandler HttpHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        EnableMultipleHttp2Connections = true,
        MaxConnectionsPerServer = 100,
        AllowAutoRedirect = false
    };

    /// <summary>
    /// Singleton HttpClient configured with <see cref="HttpHandler"/>.
    /// </summary>
    public static readonly HttpClient HttpClient = new(HttpHandler, disposeHandler: false)
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    /// <summary>
    /// Fixed plugin GUID.
    /// </summary>
    public const string PluginGuid = "1278dbd6-8399-46c0-8e64-31f2d9e022ae";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "QuickSTRM";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public override string Description => "Eliminates ffprobe latency when playing .strm files for near-instant playback start";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace!;
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = ns + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "bolt"
            },
            new PluginPageInfo
            {
                Name = "QuickStrm.js",
                EmbeddedResourcePath = ns + ".Configuration.configPage.js",
                EnableInMainMenu = false
            }
        };
    }
}
