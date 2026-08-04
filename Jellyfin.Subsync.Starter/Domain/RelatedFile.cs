namespace Jellyfin.Subsync.Starter.Domain
{
    internal class RelatedFile
    {
        internal required FileType Type { get; set; }
        internal required string FilePath { get; set; }
    }
}