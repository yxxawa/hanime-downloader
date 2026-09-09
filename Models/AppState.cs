namespace Hanime1Downloader.CSharp.Models;

public sealed class AppState
{
    public string CookieHeader { get; set; } = string.Empty;
    public string BrowserVersion { get; set; } = string.Empty;
    public List<BrowserCookieRecord> Cookies { get; set; } = [];
}
