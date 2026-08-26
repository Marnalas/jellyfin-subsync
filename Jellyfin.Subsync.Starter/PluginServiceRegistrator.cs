using Jellyfin.Subsync.Starter.Configuration;
using Jellyfin.Subsync.Starter.Infrastructure;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter;

/// <summary>
/// Located by Jellyfin at server startup and used to register services
/// that need constructor arguments Jellyfin's DI container can't invent
/// on its own (here, SkipCache's on-disk data folder).
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IPluginConfigurationProvider, PluginConfigurationProvider>();

        serviceCollection.AddSingleton<ISkipCache>(provider =>
        {
            var applicationPaths = provider.GetRequiredService<IApplicationPaths>();
            var dataFolder = Path.Combine(applicationPaths.DataPath, "subsync-starter");
            var logger = provider.GetRequiredService<ILogger<SkipCache>>();
            return new SkipCache(dataFolder, logger);
        });
            
        // SubsyncClient asks the factory per request.
        serviceCollection.AddHttpClient(nameof(SubsyncClient));

        serviceCollection.AddSingleton<ISubsyncClient>(provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var logger = provider.GetRequiredService<ILogger<SubsyncClient>>();
            return new SubsyncClient(httpClientFactory, logger);
        });

        // Singleton (not per-sweep) so the ref-count in FolderChangeSuppressor
        // stays correct even if two sweeps somehow overlap.
        serviceCollection.AddSingleton<IFolderChangeSuppressor>(provider =>
            new FolderChangeSuppressor(provider.GetRequiredService<ILibraryMonitor>()));
    }
}