namespace Hanime1Downloader.CSharp.Models;

public sealed class SearchPageResult
{
    public int CurrentPage { get; init; }
    public int TotalPages { get; init; }
    public required IReadOnlyList<VideoSummary> Results { get; init; }
}
