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

    /// <summary>
    /// 尝试仅用 download 页构建详情（源 + 标题 + 封面）。
    /// 解析不到源时返回 null（由调用方回落 watch 页）；挑战/403 等异常直接传播，
    /// 走与 watch 页相同的 Cloudflare 恢复流程，避免双重等待。
    /// </summary>
    private async Task<VideoDetails?> TryLoadFromDownloadPageAsync(string videoId, VideoDetailsLoadOptions loadOptions, CancellationToken cancellationToken, bool forceRefresh)
    {
        var downloadResponse = await FetchHtmlAsync($"download?v={videoId}", cancellationToken, forceRefresh);
        EnsureNotBlocked(downloadResponse);

        var details = new VideoDetails
        {
            VideoId = videoId,
            Title = ParseDownloadPageTitle(downloadResponse.Title, videoId),
            Url = $"{_siteBase}/watch?v={videoId}",
            LoadOptions = loadOptions
        };
        AppendSourcesFromDownloadPage(details.Sources, downloadResponse.Html);

        if (loadOptions.HasFlag(VideoDetailsLoadOptions.Cover))
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(downloadResponse.Html);
            var coverNode = doc.DocumentNode.SelectSingleNode("//*[@property='og:image']")
                            ?? doc.DocumentNode.SelectSingleNode("//meta[@name='og:image']");
            details.CoverUrl = ExtractCoverUrl(coverNode);
        }

        if (details.Sources.Count == 0)
        {
            // 下载页未能解析到源（布局变化或异常页）：让调用方回落 watch 页。
            Debug.WriteLine($"[details] download-page has no sources, fallback to watch: {videoId}");
            return null;
        }

        details.Sources = details.Sources
            .DistinctBy(item => item.Url)
            .OrderByDescending(item => item.Quality)
            .ThenBy(item => item.Type.Contains("mp4", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        Debug.WriteLine($"[details] loaded from download page: {videoId} sources={details.Sources.Count} bytes={downloadResponse.Html.Length}");
        AppLogger.InfoThrottled("details", $"轻量加载 download 页: {videoId} sources={details.Sources.Count} bytes={downloadResponse.Html.Length}", TimeSpan.FromSeconds(3));
        return details;
    }

    /// <summary>清洗 download 页标题："下載 XXX - H動漫/裏番/線上看 - Hanime1.me" → "XXX"。</summary>
    private static string ParseDownloadPageTitle(string? rawTitle, string videoId)
    {
        var title = HtmlEntity.DeEntitize(rawTitle ?? string.Empty).Trim();
        // 站点标题中的分隔符是 &amp;nbsp;（ ），统一替换为普通空格后再处理。
        title = title.Replace(' ', ' ');
        if (title.StartsWith("下載 ", StringComparison.OrdinalIgnoreCase))
        {
            title = title[3..].Trim();
        }
        else if (title.StartsWith("下载 ", StringComparison.OrdinalIgnoreCase))
        {
            title = title[3..].Trim();
        }

        var separatorIndex = title.IndexOf(" - ", StringComparison.OrdinalIgnoreCase);
        if (separatorIndex > 0)
        {
            title = title[..separatorIndex].Trim();
        }

        return ToDisplayText(title, $"视频 {videoId}");
    }

    private VideoDetails ParseWatchDetails(string videoId, BrowserFetchResult watchResponse, VideoDetailsLoadOptions loadOptions)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(watchResponse.Html);

        var titleNode = doc.DocumentNode.SelectSingleNode("//*[@id='shareBtn-title']")
                        ?? doc.DocumentNode.SelectSingleNode("//title");
        var title = ToDisplayText(titleNode?.InnerText?.Trim(), $"视频 {videoId}");

        var details = new VideoDetails
        {
            VideoId = videoId,
            Title = title,
            Url = $"{_siteBase}/watch?v={videoId}",
            LoadOptions = loadOptions
        };

        if (loadOptions.HasFlag(VideoDetailsLoadOptions.Cover))
        {
            var coverNode = doc.DocumentNode.SelectSingleNode("//*[@property='og:image']")
                            ?? doc.DocumentNode.SelectSingleNode("//meta[@name='og:image']")
                            ?? doc.DocumentNode.SelectSingleNode("//img[contains(@class, 'plyr__poster') or contains(@class, 'cover') or contains(@class, 'poster')]");
            details.CoverUrl = ExtractCoverUrl(coverNode);
        }

        if (loadOptions.HasFlag(VideoDetailsLoadOptions.Meta))
        {
            var infoPanel = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'video-description-panel')]");
            var infoText = HtmlEntity.DeEntitize(infoPanel?.InnerText?.Trim() ?? string.Empty);
            details.UploadDate = ToDisplayText(ExtractFirstMatch(infoText, DateRegex()));
            details.Views = ToDisplayText(ExtractFirstMatch(infoText, ViewsRegex()));
            details.Duration = ToDisplayText(doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'card-mobile-duration')]")?.InnerText?.Trim());
            details.Likes = ToDisplayText(doc.DocumentNode.SelectSingleNode("//*[@id='video-like-btn']")?.InnerText?.Trim());
            details.Description = ToDisplayText(doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'video-caption-text')]")?.InnerText?.Trim());
        }

        if (loadOptions.HasFlag(VideoDetailsLoadOptions.Tags))
        {
            details.Tags = doc.DocumentNode.SelectNodes("//*[contains(@class, 'single-video-tag')]//a[@href]")?
                .Select(node => ToDisplayText(node.InnerText.Trim()).TrimStart('#'))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct()
                .ToList() ?? [];
        }

        if (loadOptions.HasFlag(VideoDetailsLoadOptions.RelatedVideos))
        {
            details.RelatedVideos = ParseRelatedVideos(doc, videoId);
        }

        if (loadOptions.HasFlag(VideoDetailsLoadOptions.Sources))
        {
            AppendSourcesFromWatchPage(details.Sources, doc, watchResponse.Html);
        }
        return details;
    }

    private VideoDetails MergeDownloadSources(VideoDetails details, string downloadHtml)
    {
        AppendSourcesFromDownloadPage(details.Sources, downloadHtml);
        return details;
    }

    private List<VideoSummary> ParseRelatedVideos(HtmlDocument doc, string currentVideoId)
    {
        var results = new List<VideoSummary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemNodes = new List<HtmlNode>();
        var relatedRoots = doc.DocumentNode.SelectNodes(
            "//*[self::div or self::section or self::ul or self::ol or self::aside or self::article][" +
            "contains(@class, 'related-watch-wrap') or contains(@class, 'related-video') or " +
            "contains(@class, 'video-related') or contains(@class, 'recommend') or " +
            "contains(@class, 'recommendation') or contains(@class, 'home-rows-videos-wrapper')]")?.ToList() ?? [];

        foreach (var root in relatedRoots)
        {
            if (TryExtractVideoId(root, ReadLinkValue(root), out _))
            {
                itemNodes.Add(root);
            }

            var links = root.SelectNodes(".//a[@href or @data-href or @data-url or @data-video-id or @data-id]")?.ToList() ?? [];
            var linkIds = links
                .Where(link => TryExtractVideoId(link, ReadLinkValue(link), out _))
                .Select(link =>
                {
                    TryExtractVideoId(link, ReadLinkValue(link), out var id);
                    return id;
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (linkIds.Count == 1 && root.GetAttributeValue("class", string.Empty).Contains("related-watch-wrap", StringComparison.OrdinalIgnoreCase))
            {
                itemNodes.Add(root);
                continue;
            }

            foreach (var link in links)
            {
                var item = FindRelatedCard(link, root);
                if (!itemNodes.Contains(item))
                {
                    itemNodes.Add(item);
                }
            }
        }

        if (itemNodes.Count == 0)
        {
            var fallbackLinks = doc.DocumentNode.SelectNodes("//a[@href or @data-href or @data-url or @data-video-id or @data-id]")?.ToList() ?? [];
            foreach (var link in fallbackLinks)
            {
                if (!TryExtractVideoId(link, ReadLinkValue(link), out var id) ||
                    string.Equals(id, currentVideoId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var item = FindRelatedCard(link, doc.DocumentNode);
                if (!itemNodes.Contains(item))
                {
                    itemNodes.Add(item);
                }
            }
        }

        foreach (var item in itemNodes)
        {
            var linkNode = FindLinkNode(item) ?? item.SelectSingleNode(".//a[@href or @data-href or @data-url]");
            var href = ReadLinkValue(linkNode ?? item);
            if (!TryExtractVideoId(item, href, out var videoId) ||
                string.Equals(videoId, currentVideoId, StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(videoId))
            {
                continue;
            }

            var title = ExtractCardTitle(item, linkNode, videoId, new[]
            {
                ".//div[contains(@class, 'home-rows-videos-title')]",
                ".//div[contains(@class, 'card-mobile-title')]",
                ".//*[contains(@class, 'related-title')]",
                ".//*[contains(@class, 'video-name')]"
            });
            var coverNode = item.SelectSingleNode(".//img[@src or @data-src or @data-original or @data-lazy-src]");
            results.Add(new VideoSummary
            {
                VideoId = videoId,
                Title = title,
                Url = $"{_siteBase}/watch?v={videoId}",
                CoverUrl = ExtractCoverUrl(coverNode)
            });
        }

        return results;
    }

    private static HtmlNode FindRelatedCard(HtmlNode link, HtmlNode root)
    {
        HtmlNode? nearestBlock = null;
        for (var current = link; current is not null && !ReferenceEquals(current, root); current = current.ParentNode)
        {
            if (current.Name is "div" or "li" or "article" or "section")
            {
                nearestBlock ??= current;
                var className = current.GetAttributeValue("class", string.Empty);
                if (className.Contains("card", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("video", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("related", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("recommend", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("item", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("tile", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("watch", StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }
            }
        }

        return nearestBlock ?? link;
    }

    private void AppendSourcesFromWatchPage(List<VideoSource> sources, HtmlDocument doc, string html)
    {
        var sourceNodes = doc.DocumentNode.SelectNodes("//video[@id='player']//source")?.ToList()
                          ?? new List<HtmlNode>();
        foreach (var source in sourceNodes)
        {
            var src = source.GetAttributeValue("src", string.Empty);
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            AppendSource(
                sources,
                src,
                ParseQuality(source.GetAttributeValue("size", string.Empty)),
                source.GetAttributeValue("type", "video/mp4"));
        }

        foreach (Match match in SourceRegex().Matches(html))
        {
            AppendSource(sources, HttpUtility.HtmlDecode(match.Value));
        }

        foreach (Match match in JsSourceRegex().Matches(html))
        {
            AppendSource(sources, HttpUtility.HtmlDecode(match.Groups[1].Value));
        }

        foreach (Match match in ScriptUrlRegex().Matches(html))
        {
            AppendSource(sources, HttpUtility.HtmlDecode(match.Groups[1].Value));
        }
    }

    private void AppendSourcesFromDownloadPage(List<VideoSource> sources, string downloadHtml)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(downloadHtml);

        var dataUrlNodes = doc.DocumentNode.SelectNodes("//a[@data-url]")?.ToList() ?? new List<HtmlNode>();
        foreach (var node in dataUrlNodes)
        {
            var dataUrl = node.GetAttributeValue("data-url", string.Empty);
            var quality = ParseQualityFromText(node.ParentNode?.InnerText ?? node.InnerText);
            AppendSource(sources, HttpUtility.HtmlDecode(dataUrl), quality);
        }

        var hrefNodes = doc.DocumentNode.SelectNodes("//a[@href]")?.ToList() ?? new List<HtmlNode>();
        foreach (var node in hrefNodes)
        {
            var href = node.GetAttributeValue("href", string.Empty);
            if (!LooksLikeMediaUrl(href))
            {
                continue;
            }

            var quality = ParseQualityFromText(node.InnerText);
            AppendSource(sources, HttpUtility.HtmlDecode(href), quality);
        }

        var sourceNodes = doc.DocumentNode.SelectNodes("//video//source[@src]")?.ToList() ?? new List<HtmlNode>();
        foreach (var node in sourceNodes)
        {
            var src = node.GetAttributeValue("src", string.Empty);
            var quality = ParseQuality(node.GetAttributeValue("size", string.Empty));
            AppendSource(sources, HttpUtility.HtmlDecode(src), quality, node.GetAttributeValue("type", string.Empty));
        }

        foreach (Match match in SourceRegex().Matches(downloadHtml))
        {
            AppendSource(sources, HttpUtility.HtmlDecode(match.Value));
        }

        foreach (Match match in DownloadUrlRegex().Matches(downloadHtml))
        {
            AppendSource(sources, HttpUtility.HtmlDecode(match.Groups[1].Value));
        }
    }

    private void AppendSource(List<VideoSource> sources, string rawUrl, int? quality = null, string? type = null)
    {
        var decoded = HttpUtility.HtmlDecode(rawUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return;
        }

        var hintedByType = !string.IsNullOrWhiteSpace(type) && type.Contains("video", StringComparison.OrdinalIgnoreCase);
        if (!hintedByType && !LooksLikeMediaUrl(decoded))
        {
            return;
        }

        var url = NormalizeUrl(decoded);
        if (sources.Any(item => item.Url == url))
        {
            return;
        }

        sources.Add(new VideoSource
        {
            Url = url,
            Type = string.IsNullOrWhiteSpace(type)
                ? (url.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ? "application/x-mpegURL" : "video/mp4")
                : type,
            Quality = quality ?? ParseQualityFromText(url) ?? 0
        });
    }

    private static bool LooksLikeMediaUrl(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        var lowered = rawUrl.Trim().ToLowerInvariant();
        if (lowered.Contains("cdnjs.cloudflare.com") || lowered.Contains("cdn.jsdelivr.net"))
        {
            return false;
        }

        var pathPart = lowered.Split('?')[0];
        return pathPart.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || pathPart.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private string NormalizeUrl(string src)
    {
        if (src.StartsWith("//", StringComparison.OrdinalIgnoreCase))
        {
            return $"https:{src}";
        }

        return src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? src : $"{_siteBase}{src}";
    }

    private static string ExtractFirstMatch(string input, Regex regex)
    {
        var match = regex.Match(input ?? string.Empty);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static string ToDisplayText(string? value, string fallback = "")
    {
        var text = HtmlEntity.DeEntitize(value?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = fallback;
        }

        return SimplifiedChineseConverter.ToSimplified(text);
    }

    private string ExtractCoverUrl(HtmlNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        var rawUrl = node.Name.Equals("meta", StringComparison.OrdinalIgnoreCase)
            ? node.GetAttributeValue("content", string.Empty)
            : node.GetAttributeValue("src",
                node.GetAttributeValue("data-src",
                node.GetAttributeValue("data-original",
                node.GetAttributeValue("data-lazy-src", string.Empty))));
        return string.IsNullOrWhiteSpace(rawUrl) ? string.Empty : NormalizeUrl(HttpUtility.HtmlDecode(rawUrl));
    }

    private static int? ParseQuality(string raw)
    {
        return int.TryParse(raw.Replace("p", string.Empty, StringComparison.OrdinalIgnoreCase), out var quality)
            ? quality
            : null;
    }

    private static int? ParseQualityFromText(string raw)
    {
        var match = QualityRegex().Match(raw ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var quality) ? quality : null;
    }
}
