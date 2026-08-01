using System;
using System.Collections.Generic;
using Jellyfin.Subsync.Starter.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Subsync.Starter
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static Plugin? Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => "Subsync";

        public override Guid Id => Guid.Parse("6e9cb927-95fc-4ab9-8267-c896060ae50e");

        public override string Description =>
            "Automatically syncs subtitles against their video using a GPU-accelerated ffsubsync sidecar.";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = "Subsync",
                EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace),
            };
        }
    }
}

