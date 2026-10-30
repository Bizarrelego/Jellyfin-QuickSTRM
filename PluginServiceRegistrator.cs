using System.Linq;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuickSTRM.ScheduledTasks;

namespace QuickSTRM;

/// <summary>
/// Registers plugin services via Jellyfin's DI pipeline.
/// Decorates <see cref="IMediaSourceManager"/> with <see cref="MediaSourceInterceptor"/>
/// and registers <see cref="ProbeService"/> as a hosted singleton.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register TelemetryService as singleton for native activity logging and action ledger
        serviceCollection.AddSingleton<TelemetryService>();

        // Register ProbeService as singleton + hosted service for ItemAdded/ItemUpdated events and queue lifecycle
        serviceCollection.AddSingleton<ProbeService>(sp => new ProbeService(
            sp.GetRequiredService<ILibraryManager>(),
            sp.GetRequiredService<MediaBrowser.Controller.Providers.IDirectoryService>(),
            sp.GetRequiredService<MediaBrowser.Controller.MediaEncoding.IMediaEncoder>(),
            sp.GetService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
            sp.GetRequiredService<MediaBrowser.Model.Activity.IActivityManager>(),
            sp.GetRequiredService<ILogger<ProbeService>>(),
            sp.GetRequiredService<TelemetryService>(),
            sp.GetService<MediaBrowser.Controller.Persistence.IItemRepository>(),
            sp.GetService<IMediaSourceManager>(),
            sp.GetService<AutoHealService>(),
            null,
            sp.GetService<VfsPrewarmService>(),
            sp.GetService<MediaBrowser.Controller.Persistence.IMediaStreamRepository>()));
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ProbeService>());

        // Register AutoHealService as singleton + hosted service for event-driven list pruning
        serviceCollection.AddSingleton<AutoHealService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AutoHealService>());

        // Register SessionMonitorService as singleton + hosted service for next-episode pre-warming
        serviceCollection.AddSingleton<SessionMonitorService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SessionMonitorService>());

        // Register VfsPrewarmService as singleton
        serviceCollection.AddSingleton<VfsPrewarmService>();

        // Register PredictivePrewarmMiddleware as transient
        serviceCollection.AddTransient<PredictivePrewarmMiddleware>();

        // Register IStartupFilter to insert middleware before MVC routing endpoints
        serviceCollection.AddTransient<IStartupFilter, PredictivePrewarmStartupFilter>();

        // Register ProactiveSweeperTask as transient IScheduledTask so Jellyfin's task engine picks it up on startup
        serviceCollection.AddTransient<IScheduledTask, ProactiveSweeperTask>();

        // Register StrmProbeTask as IScheduledTask so Jellyfin displays it in Dashboard -> Scheduled Tasks
        serviceCollection.AddTransient<IScheduledTask, StrmProbeTask>();

        // Decorate IMediaSourceManager with our interceptor
        var descriptor = serviceCollection.FirstOrDefault(d => d.ServiceType == typeof(IMediaSourceManager));
        if (descriptor is null)
        {
            return;
        }

        serviceCollection.Remove(descriptor);
        serviceCollection.Add(new ServiceDescriptor(
            typeof(IMediaSourceManager),
            sp =>
            {
                // Resolve the original implementation regardless of registration style
                IMediaSourceManager inner;
                if (descriptor.ImplementationInstance is not null)
                {
                    inner = (IMediaSourceManager)descriptor.ImplementationInstance;
                }
                else if (descriptor.ImplementationFactory is not null)
                {
                    inner = (IMediaSourceManager)descriptor.ImplementationFactory(sp);
                }
                else
                {
                    inner = (IMediaSourceManager)ActivatorUtilities.CreateInstance(
                        sp, descriptor.ImplementationType!);
                }

                return new MediaSourceInterceptor(
                    inner,
                    sp.GetRequiredService<ILogger<MediaSourceInterceptor>>(),
                    // lazy resolution avoids a DI cycle with ILibraryManager at container build time
                    () => sp.GetRequiredService<ProbeService>());
            },
            descriptor.Lifetime));
    }
}
