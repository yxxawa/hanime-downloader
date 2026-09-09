namespace Hanime1Downloader.CSharp.Models;

[Flags]
public enum VideoDetailsLoadOptions
{
    Basic = 1 << 0,
    Cover = 1 << 1,
    Meta = 1 << 2,
    Tags = 1 << 3,
    RelatedVideos = 1 << 4,
    Sources = 1 << 5,
    All = Basic | Cover | Meta | Tags | RelatedVideos | Sources
}
