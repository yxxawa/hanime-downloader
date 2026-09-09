namespace Hanime1Downloader.CSharp.Models;

public sealed class BrowserFetchResult
{
    public int Status { get; init; }
    public string Url { get; init; } = string.Empty;
    public string Html { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}
