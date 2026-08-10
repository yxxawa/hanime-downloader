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
    private static readonly TimeSpan SearchCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DetailsCacheDuration = TimeSpan.FromMinutes(5);
    private readonly CloudflareWindow _browserWindow;
    private readonly HttpClient? _httpClient;
    private readonly string _siteBase;
    private readonly Uri _siteBaseUri;
    private readonly ConcurrentDictionary<string, HtmlCacheEntry> _htmlCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<BrowserFetchResult>>> _htmlInFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DirectHtmlTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DirectHttpCooldown = TimeSpan.FromSeconds(20);
    private long _directHttpDisabledUntilTicks;

    public HanimeApiClient(CloudflareWindow browserWindow, string siteHost = "hanime1.me")
        : this(browserWindow, null, siteHost)
    {
    }

    public HanimeApiClient(CloudflareWindow browserWindow, HttpClient? httpClient, string siteHost = "hanime1.me")
    {
        _browserWindow = browserWindow;
        _httpClient = httpClient;
        _siteBase = $"https://{siteHost}";
        _siteBaseUri = new Uri($"{_siteBase}/");
    }

    public async Task<SearchPageResult> SearchAsync(string keyword, int page = 1, SearchFilterOptions? filters = null, CancellationToken cancellationToken = default)
    {
        var operationId = $"search-{Environment.TickCount64}";
        var normalizedPage = Math.Max(1, page);
        var queryString = BuildSearchQueryString(keyword, normalizedPage, filters);
        Debug.WriteLine($"[{operationId}] Search fetch: page={normalizedPage}, keyword={keyword}");
        var response = await FetchHtmlAsync($"search?{queryString}", cancellationToken);
        EnsureNotBlocked(response);
        var result = await Task.Run(() => ParseSearchResult(response, normalizedPage), cancellationToken);
        Debug.WriteLine($"[{operationId}] Search parsed: page={result.CurrentPage}, total={result.TotalPages}, count={result.Results.Count}");
        return result;
    }

    private SearchPageResult ParseSearchResult(BrowserFetchResult response, int normalizedPage)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(response.Html);
        var results = new List<VideoSummary>();
        var seen = new HashSet<string>();

        var normalContainers = doc.DocumentNode.SelectNodes("//*[contains(@class, 'content-padding-new')]")?.ToList() ?? [];
        foreach (var container in normalContainers)
        {
            var cards = container.SelectNodes(".//div[starts-with(@class, 'horizontal-card') or contains(@class, 'horizontal-card')]")?.ToList() ?? [];
            foreach (var card in cards)
            {
                AppendNormalSearchItem(results, seen, card);
            }
        }

        var parsedCurrentPage = ParseCurrentPage(doc, normalizedPage);
        var parsedTotalPages = ParseTotalPages(doc, parsedCurrentPage);
        var hasNextPage = HasNextPage(doc, parsedCurrentPage);

        if (results.Count > 0)
        {
            return new SearchPageResult
            {
                CurrentPage = parsedCurrentPage,
                TotalPages = hasNextPage ? Math.Max(parsedTotalPages, parsedCurrentPage + 1) : parsedTotalPages,
                Results = results
            };
        }

        var simplifiedContainers = doc.DocumentNode.SelectNodes("//*[contains(@class, 'home-rows-videos-wrapper')]")?.ToList() ?? [];
        foreach (var container in simplifiedContainers)
        {
            var entries = container.ChildNodes.Where(node => node.NodeType == HtmlNodeType.Element).ToList();
            foreach (var entry in entries)
            {
                AppendSimplifiedSearchItem(results, seen, entry);
            }
        }

        if (results.Count == 0)
        {
            var fallbackLinks = doc.DocumentNode.SelectNodes("//a[@href]")?.ToList() ?? [];
            foreach (var link in fallbackLinks)
            {
                AppendSimplifiedSearchItem(results, seen, link);
            }
        }

        if (results.Count == 0)
        {
            var preview = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText ?? string.Empty).Trim();
            preview = preview.Length > 120 ? preview[..120] : preview;
            throw new InvalidOperationException($"搜索页已打开但未解析到结果。status={response.Status}, url={response.Url}, title={response.Title}, preview={preview}");
        }

        return new SearchPageResult
        {
            CurrentPage = parsedCurrentPage,
            TotalPages = hasNextPage ? Math.Max(parsedTotalPages, parsedCurrentPage + 1) : parsedTotalPages,
            Results = results
        };
    }

    private static string BuildSearchQueryString(string keyword, int page, SearchFilterOptions? filters)
    {
        var parameters = HttpUtility.ParseQueryString(string.Empty);
        parameters["query"] = keyword;
        parameters["page"] = page.ToString();

        if (filters is not null)
        {
            if (!string.IsNullOrWhiteSpace(filters.Genre))
            {
                parameters["genre"] = filters.Genre;
            }

            if (!string.IsNullOrWhiteSpace(filters.Sort))
            {
                parameters["sort"] = filters.Sort;
            }

            if (!string.IsNullOrWhiteSpace(filters.Date))
            {
                parameters["date"] = filters.Date;
            }

            if (!string.IsNullOrWhiteSpace(filters.Duration))
            {
                parameters["duration"] = filters.Duration;
            }

            if (filters.Broad)
            {
                parameters["broad"] = "on";
            }

            if (filters.Tags.Count > 0)
            {
                foreach (var tag in filters.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)))
                {
                    parameters.Add("tags[]", tag);
                }
            }
        }

        return parameters.ToString() ?? string.Empty;
    }

    private static int ParseCurrentPage(HtmlDocument doc, int fallbackPage)
    {
        var currentNode = doc.DocumentNode.SelectSingleNode("//ul[contains(@class, 'pagination')]//*[contains(@class, 'active')]//*[self::a or self::span][contains(@class, 'page-link')]")
                         ?? doc.DocumentNode.SelectSingleNode("//ul[contains(@class, 'pagination')]//*[contains(@class, 'active') and self::a or self::span][contains(@class, 'page-link')]");
        if (currentNode is not null)
        {
            var currentText = HtmlEntity.DeEntitize(currentNode.InnerText ?? string.Empty).Trim();
            if (int.TryParse(currentText, out var currentPage))
            {
                return currentPage;
            }
        }

        return fallbackPage;
    }

    private static int ParseTotalPages(HtmlDocument doc, int currentPage)
    {
        var pageNumbers = ExtractPageNumbers(doc);
        var hasNextPage = HasNextPage(doc, currentPage);

        if (pageNumbers.Count > 0)
        {
            var calculatedTotalPages = pageNumbers.Max();
            if (hasNextPage && currentPage >= calculatedTotalPages)
            {
                calculatedTotalPages = currentPage + 1;
            }

            return Math.Max(1, calculatedTotalPages);
        }

        return hasNextPage ? currentPage + 1 : Math.Max(1, currentPage);
    }

    private static List<int> ExtractPageNumbers(HtmlDocument doc)
    {
        var pageNumbers = new HashSet<int>();
        var paginationNodes = doc.DocumentNode.SelectNodes("//ul[contains(@class, 'pagination')]//*[self::a or self::span][contains(@class, 'page-link')]")?.ToList() ?? [];
        foreach (var node in paginationNodes)
        {
            var href = node.GetAttributeValue("href", string.Empty);
            var pageMatch = PageNumberRegex().Match(href);
            if (pageMatch.Success && int.TryParse(pageMatch.Groups[1].Value, out var pageFromHref))
            {
                pageNumbers.Add(pageFromHref);
            }

            var text = HtmlEntity.DeEntitize(node.InnerText ?? string.Empty).Trim();
            if (int.TryParse(text, out var pageFromText))
            {
                pageNumbers.Add(pageFromText);
            }
        }

        return pageNumbers.OrderBy(page => page).ToList();
    }

    private static bool HasNextPage(HtmlDocument doc, int currentPage)
    {
        var paginationLinks = doc.DocumentNode.SelectNodes("//ul[contains(@class, 'pagination')]//a[@href]")?.ToList() ?? [];
        foreach (var link in paginationLinks)
        {
            var text = HtmlEntity.DeEntitize(link.InnerText ?? string.Empty).Trim();
            var href = link.GetAttributeValue("href", string.Empty);
            var className = link.GetAttributeValue("class", string.Empty);
            var rel = link.GetAttributeValue("rel", string.Empty);
            var pageMatch = PageNumberRegex().Match(href);
            if (pageMatch.Success && int.TryParse(pageMatch.Groups[1].Value, out var pageNumber) && pageNumber > currentPage)
            {
                return true;
            }

            if (NextPageTextRegex().IsMatch(text) ||
                className.Contains("next", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("next", StringComparison.OrdinalIgnoreCase) ||
                NextPageHrefRegex().IsMatch(href))
            {
                return true;
            }
        }

        return false;
    }

    private void AppendNormalSearchItem(List<VideoSummary> results, HashSet<string> seen, HtmlNode card)
        => AppendSearchItem(results, seen, card, [
            ".//div[contains(@class, 'title')]",
            ".//h4[contains(@class, 'video-title')]",
            ".//*[@title]"
        ], resolveHref: true);

    private void AppendSimplifiedSearchItem(List<VideoSummary> results, HashSet<string> seen, HtmlNode node)
        => AppendSearchItem(results, seen, node, [
            ".//div[contains(@class, 'home-rows-videos-title')]",
            ".//div[contains(@class, 'title')]",
            ".//h4[contains(@class, 'video-title')]",
            ".//*[@title]"
        ], resolveHref: false);

    private void AppendSearchItem(List<VideoSummary> results, HashSet<string> seen, HtmlNode node, string[] titleSelectors, bool resolveHref)
    {
        var linkNode = FindLinkNode(node);
        var href = ReadLinkValue(linkNode ?? node);
        if (!resolveHref && linkNode is not null)
        {
            node = linkNode.ParentNode is not null && !IsLinkNode(linkNode.ParentNode)
                ? linkNode.ParentNode
                : linkNode;
        }

        if (!TryExtractVideoId(linkNode ?? node, href, out var id) || !seen.Add(id))
        {
            return;
        }

        var coverNode = node.SelectSingleNode(".//img[@src or @data-src or @data-original or @data-lazy-src]")
                       ?? linkNode?.SelectSingleNode(".//img[@src or @data-src or @data-original or @data-lazy-src]");
        var title = ExtractCardTitle(node, linkNode, id, titleSelectors);
        results.Add(new VideoSummary
        {
            VideoId = id,
            Title = title,
            Url = $"{_siteBase}/watch?v={id}",
            CoverUrl = ExtractCoverUrl(coverNode)
        });
    }

    private static HtmlNode? FindLinkNode(HtmlNode node)
    {
        if (IsLinkNode(node))
        {
            return node;
        }

        return node.SelectSingleNode(".//a[@href or @data-href or @data-url or @data-video-id or @data-id]")
               ?? node.SelectSingleNode(".//*[@data-video-id or @data-id or @data-href or @data-url]");
    }

    private static bool IsLinkNode(HtmlNode node)
    {
        return node.Name.Equals("a", StringComparison.OrdinalIgnoreCase) ||
               !string.IsNullOrWhiteSpace(node.GetAttributeValue("href", string.Empty)) ||
               !string.IsNullOrWhiteSpace(node.GetAttributeValue("data-href", string.Empty)) ||
               !string.IsNullOrWhiteSpace(node.GetAttributeValue("data-url", string.Empty));
    }

    private static string ReadLinkValue(HtmlNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        foreach (var attribute in new[] { "href", "data-href", "data-url", "data-link", "url" })
        {
            var value = node.GetAttributeValue(attribute, string.Empty);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static bool TryExtractVideoId(HtmlNode? node, string? rawUrl, out string videoId)
    {
        foreach (var attribute in new[] { "data-video-id", "data-video", "video-id", "data-id", "data-v" })
        {
            var value = node?.GetAttributeValue(attribute, string.Empty);
            if (TryNormalizeVideoId(value, out videoId))
            {
                return true;
            }
        }

        foreach (var value in new[] { rawUrl, ReadLinkValue(node) })
        {
            if (TryNormalizeVideoId(value, out videoId))
            {
                return true;
            }
        }

        videoId = string.Empty;
        return false;
    }

    private static bool TryNormalizeVideoId(string? rawValue, out string videoId)
    {
        var value = HttpUtility.UrlDecode(HttpUtility.HtmlDecode(rawValue ?? string.Empty))?.Trim() ?? string.Empty;
        if (value.Length > 0 && value.All(char.IsDigit))
        {
            videoId = value;
            return true;
        }

        var match = VideoIdRegex().Match(value);
        if (match.Success)
        {
            videoId = match.Groups[1].Value;
            return true;
        }

        videoId = string.Empty;
        return false;
    }

    private static string ExtractCardTitle(HtmlNode item, HtmlNode? linkNode, string videoId, IEnumerable<string> titleSelectors)
    {
        foreach (var selector in titleSelectors)
        {
            var title = ExtractUsableTitle(item.SelectSingleNode(selector), videoId);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        foreach (var selector in new[]
        {
            ".//*[contains(@class, 'video-title')]",
            ".//*[contains(@class, 'title')]",
            ".//h1",
            ".//h2",
            ".//h3",
            ".//h4"
        })
        {
            var title = ExtractUsableTitle(item.SelectSingleNode(selector), videoId);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        var metadataNodes = item.SelectNodes(".//*[@data-title or @data-name or @title or @aria-label or @alt]")?.ToList() ?? [];
        foreach (var node in metadataNodes)
        {
            var title = ExtractUsableTitle(node, videoId);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        foreach (var node in new[] { linkNode, item })
        {
            var title = ExtractUsableTitle(node, videoId);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        return $"视频 {videoId}";
    }

    private static string ExtractUsableTitle(HtmlNode? node, string videoId)
    {
        if (node is null)
        {
            return string.Empty;
        }

        foreach (var attribute in new[] { "data-title", "data-name", "title", "aria-label", "alt" })
        {
            var title = ToDisplayText(node.GetAttributeValue(attribute, string.Empty));
            if (IsUsableTitle(title, videoId))
            {
                return title;
            }
        }

        var text = ToDisplayText(node.InnerText);
        return IsUsableTitle(text, videoId) ? text : string.Empty;
    }

    private static bool IsUsableTitle(string? title, string videoId)
    {
        var normalized = string.Join(" ", (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        return !compact.Equals(videoId, StringComparison.OrdinalIgnoreCase) &&
               !compact.Equals($"视频{videoId}", StringComparison.OrdinalIgnoreCase) &&
               !compact.Equals($"video{videoId}", StringComparison.OrdinalIgnoreCase) &&
               !compact.Equals("播放", StringComparison.OrdinalIgnoreCase) &&
               !compact.Equals("观看", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<VideoDetails?> GetDetailsAsync(string videoId, VideoDetailsLoadOptions loadOptions = VideoDetailsLoadOptions.Basic | VideoDetailsLoadOptions.Sources, CancellationToken cancellationToken = default)
    {
        var watchResponse = await FetchHtmlAsync($"watch?v={videoId}", cancellationToken);
        EnsureNotBlocked(watchResponse);

        BrowserFetchResult? downloadResponse = null;
        Task<BrowserFetchResult>? downloadTask = null;
        if (loadOptions.HasFlag(VideoDetailsLoadOptions.Sources) && !HasEmbeddedSourceHint(watchResponse.Html))
        {
            // Start the fallback request while the watch-page DOM is parsed. The fallback is only
            // prefetched when the watch HTML has no source marker, avoiding an extra request for the
            // common case where the player already exposes playable URLs.
            downloadTask = FetchHtmlAsync($"download?v={videoId}", cancellationToken);
        }

        if (downloadTask is not null)
        {
            _ = ObserveAsync(downloadTask);
        }

        var parsedDetails = await Task.Run(() => ParseWatchDetails(videoId, watchResponse, loadOptions), cancellationToken);
        if (loadOptions.HasFlag(VideoDetailsLoadOptions.Sources) && parsedDetails.Sources.Count == 0)
        {
            downloadResponse = downloadTask is not null
                ? await downloadTask
                : await FetchHtmlAsync($"download?v={videoId}", cancellationToken);
            EnsureNotBlocked(downloadResponse);
            parsedDetails = await Task.Run(() => MergeDownloadSources(parsedDetails, downloadResponse.Html), cancellationToken);
        }

        if (loadOptions.HasFlag(VideoDetailsLoadOptions.Sources))
        {
            parsedDetails.Sources = parsedDetails.Sources
                .DistinctBy(item => item.Url)
                .OrderByDescending(item => item.Quality)
                .ThenBy(item => item.Type.Contains("mp4", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
        }

        parsedDetails.LoadOptions = loadOptions;
        return parsedDetails;
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
            Quality = quality ?? ParseQualityFromText(url)
        });
    }

    private async Task<BrowserFetchResult> FetchHtmlAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targetUri = new Uri(_siteBaseUri, relativeUrl);
        var cacheKey = targetUri.AbsoluteUri;
        var now = DateTimeOffset.UtcNow;
        if (_htmlCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
        {
            Debug.WriteLine($"[html-cache] hit: {cacheKey}");
            return cached.Response;
        }

        var lazy = new Lazy<Task<BrowserFetchResult>>(
            () => FetchHtmlCoreAsync(relativeUrl, targetUri, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var inFlight = _htmlInFlight.GetOrAdd(cacheKey, lazy);
        try
        {
            var response = await inFlight.Value.WaitAsync(cancellationToken);
            if (ShouldCache(response))
            {
                _htmlCache[cacheKey] = new HtmlCacheEntry(response, DateTimeOffset.UtcNow + GetCacheDuration(relativeUrl));
            }

            return response;
        }
        finally
        {
            if (inFlight.Value.IsCompleted && _htmlInFlight.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, inFlight))
            {
                _htmlInFlight.TryRemove(cacheKey, out _);
            }
        }
    }

    private async Task<BrowserFetchResult> FetchHtmlCoreAsync(string relativeUrl, Uri targetUri, CancellationToken cancellationToken)
    {
        if (_httpClient is not null && IsDirectHttpAvailable())
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, targetUri);
                request.Headers.Referrer = _siteBaseUri;
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.5));
                request.Headers.AcceptEncoding.Clear();
                request.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
                request.Headers.Remove("Sec-Fetch-Dest");
                request.Headers.Remove("Sec-Fetch-Mode");
                request.Headers.Remove("Sec-Fetch-Site");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");

                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(DirectHtmlTimeout);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
                var html = await response.Content.ReadAsStringAsync(requestTimeout.Token);
                var result = new BrowserFetchResult
                {
                    Status = (int)response.StatusCode,
                    Url = response.RequestMessage?.RequestUri?.ToString() ?? targetUri.ToString(),
                    Html = html,
                    Title = ExtractHtmlTitle(html)
                };

                if (response.IsSuccessStatusCode && !IsChallengePage(html))
                {
                    Debug.WriteLine($"[html-http] {targetUri} status={(int)response.StatusCode} bytes={html.Length}");
                    return result;
                }

                DisableDirectHttp();
                Debug.WriteLine($"[html-http] fallback to WebView2: {targetUri} status={(int)response.StatusCode} challenge={IsChallengePage(html)} cooldown={DirectHttpCooldown.TotalSeconds:0}s");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                DisableDirectHttp();
                Debug.WriteLine($"[html-http] timeout, fallback to WebView2: {targetUri} cooldown={DirectHttpCooldown.TotalSeconds:0}s");
            }
            catch (HttpRequestException ex)
            {
                DisableDirectHttp();
                Debug.WriteLine($"[html-http] request failed, fallback to WebView2: {targetUri} error={ex.Message} cooldown={DirectHttpCooldown.TotalSeconds:0}s");
            }
            catch (ObjectDisposedException)
            {
                DisableDirectHttp();
                Debug.WriteLine($"[html-http] client disposed, fallback to WebView2: {targetUri} cooldown={DirectHttpCooldown.TotalSeconds:0}s");
            }
        }

        return await _browserWindow.FetchHtmlAsync(relativeUrl, cancellationToken);
    }

    private bool IsDirectHttpAvailable()
    {
        return DateTimeOffset.UtcNow.Ticks >= Interlocked.Read(ref _directHttpDisabledUntilTicks);
    }

    private void DisableDirectHttp()
    {
        Interlocked.Exchange(ref _directHttpDisabledUntilTicks, DateTimeOffset.UtcNow.Add(DirectHttpCooldown).Ticks);
    }

    private static bool HasEmbeddedSourceHint(string html)
    {
        return html.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
               html.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("data-url", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The page is only a speculative fallback; the watch-page sources remain authoritative.
        }
    }

    private static bool ShouldCache(BrowserFetchResult response)
    {
        return response.Status is >= 200 and < 300 &&
               !string.IsNullOrWhiteSpace(response.Html) &&
               !IsChallengePage(response.Html);
    }

    private static TimeSpan GetCacheDuration(string relativeUrl)
    {
        return relativeUrl.StartsWith("search?", StringComparison.OrdinalIgnoreCase)
            ? SearchCacheDuration
            : DetailsCacheDuration;
    }

    private static string ExtractHtmlTitle(string html)
    {
        var match = HtmlTitleRegex().Match(html ?? string.Empty);
        return match.Success ? HtmlEntity.DeEntitize(match.Groups[1].Value).Trim() : string.Empty;
    }

    private static bool IsChallengePage(string html)
    {
        return html.Contains("Performing security verification", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("window._cf_chl_opt", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("challenge-form", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("cf-mitigated", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record HtmlCacheEntry(BrowserFetchResult Response, DateTimeOffset ExpiresAt);

    [GeneratedRegex("<title\\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlTitleRegex();

    private static void EnsureNotBlocked(BrowserFetchResult response)
    {
        if (response.Html.Contains("Performing security verification", StringComparison.OrdinalIgnoreCase) ||
            response.Html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
            response.Html.Contains("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase) ||
            response.Html.Contains("window._cf_chl_opt", StringComparison.OrdinalIgnoreCase) ||
            response.Html.Contains("challenge-form", StringComparison.OrdinalIgnoreCase) ||
            response.Html.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase) ||
            response.Html.Contains("cf-mitigated", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("请求被 Cloudflare 挑战页拦截，请重新验证。页面仍是 Cloudflare 验证页。");
        }

        if (response.Status == 403)
        {
            throw new InvalidOperationException("站点返回 403，当前浏览器会话未被接受。请在验证窗口中先确认主页已正常打开。");
        }
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

    private static int ParseQuality(string raw)
    {
        return int.TryParse(raw.Replace("p", string.Empty, StringComparison.OrdinalIgnoreCase), out var quality)
            ? quality
            : 0;
    }

    private static int ParseQualityFromText(string raw)
    {
        var match = QualityRegex().Match(raw ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var quality) ? quality : 0;
    }

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
