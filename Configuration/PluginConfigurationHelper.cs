
namespace Jellyfin.Subsync.Starter.Configuration
{
    internal static class PluginConfigurationHelper
    {

        private static readonly List<string> DefaultVideoExtensions =
            ["mkv", "mp4", "m4v", "avi", "ts", "mov", "wmv"];

        internal static bool NormalizeVideoExtensions(this PluginConfiguration configuration)
        {
            var extensions = configuration.VideoExtensions.Count == 0
                ? DefaultVideoExtensions
                : [.. configuration.VideoExtensions.Distinct()];
            if (!extensions.SequenceEqual(configuration.VideoExtensions))
            {
                configuration.VideoExtensions = extensions;
                return true;
            }
            return false;
        }
    }
}