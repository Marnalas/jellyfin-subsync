
namespace Jellyfin.Subsync.Starter.Configuration
{
    internal static class PluginConfigurationHelper
    {
        private static readonly List<string> DefaultSubtitleExtensions =
            ["srt", "ass", "ssa", "vtt", "sub"];

        internal static bool NormalizeSubtitleExtensions(this PluginConfiguration configuration)
        {
            var normalized = Normalize(configuration.SubtitleExtensions, DefaultSubtitleExtensions);
            if (normalized is null)
                return false;
            configuration.SubtitleExtensions = normalized;
            return true;
        }

        private static List<string>? Normalize(List<string> current, List<string> defaults)
        {
            var normalized = current.Count == 0 ? defaults : [.. current.Distinct()];
            return normalized.SequenceEqual(current) ? null : normalized;
        }
    }
}