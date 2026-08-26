using Jellyfin.Subsync.Starter.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Subsync.Starter
{
    public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static Plugin? Instance { get; private set; }

        public override string Name => "Subsync";

        public override Guid Id => Guid.Parse("6e9cb927-95fc-4ab9-8267-c896060ae50e");

        public override string Description =>
            "Automatically syncs subtitles against their video using a ffsubsync sidecar.";

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;

            if (Configuration.NormalizeSubtitleExtensions())
                SaveConfiguration();
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = "Subsync",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu = true
            };
            yield return new PluginPageInfo
            {
                Name = "Cache",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configCachePage.html",
                MenuIcon = "closed_caption"
            };
        }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            if (configuration is PluginConfiguration pluginConfiguration)
                pluginConfiguration.DeriveWatchedPathsMaps();

            base.UpdateConfiguration(configuration);
        }
    }
}
