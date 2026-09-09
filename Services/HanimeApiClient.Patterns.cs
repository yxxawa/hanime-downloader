using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Views;
using HtmlAgilityPack;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Web;

namespace Hanime1Downloader.CSharp.Services;

public sealed partial class HanimeApiClient
{

    [GeneratedRegex("<title\\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlTitleRegex();

    [GeneratedRegex(@"(?:[?&](?:v|id|video[_-]?id|videoId)=|/(?:watch|video|videos)(?:/|=)|(?:^|[^\d])(?:video[_-]?id|vid)[=:])(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2})")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"观看次数[：:]\s*([^\s]+)")]
    private static partial Regex ViewsRegex();

    [GeneratedRegex("https?://[^\"'\\s>]+\\.(?:mp4|m3u8)[^\"'\\s>]*", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRegex();

    [GeneratedRegex("const\\s+source\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase)]
    private static partial Regex JsSourceRegex();

    [GeneratedRegex("(?:source|src)\\s*[:=]\\s*['\"](https?:\\/\\/[^'\"]+|\\/\\/[^'\"]+|[^'\"]+\\.(?:mp4|m3u8)[^'\"]*)['\"]", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptUrlRegex();

    [GeneratedRegex("data-url=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadUrlRegex();

    [GeneratedRegex(@"(\d{3,4})p", RegexOptions.IgnoreCase)]
    private static partial Regex QualityRegex();

    [GeneratedRegex(@"[?&]page=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumberRegex();

    [GeneratedRegex(@"下一頁|下一页|>|»", RegexOptions.IgnoreCase)]
    private static partial Regex NextPageTextRegex();

    [GeneratedRegex(@"next|page=\d+", RegexOptions.IgnoreCase)]
    private static partial Regex NextPageHrefRegex();
}
