namespace Jellyfin.Subsync.Starter.Domain
{
    public class RelatedFile
    {
        public required FileType Type { get; set; }
        public required string FilePath { get; set; }
    }
}