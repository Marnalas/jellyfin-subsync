using System.Text.RegularExpressions;

namespace Jellyfin.Subsync.Starter.Infrastructure
{
    internal static partial class RegularExpressions
    {
        [GeneratedRegex("""^(?<root>.*)\.\w{2,3}(\.\d{1})?\.\w{2,4}$""", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
        internal static partial Regex RootPart();
    }
}