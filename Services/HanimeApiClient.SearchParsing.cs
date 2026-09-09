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
        // 仅当 node 不是链接节点本身（例如是卡片外层容器）时才收窄到链接的父容器；
        // node 本身就是 <a> 卡片时保持原作用域，否则标题/封面会错误地取到整个列表容器的第一个元素。
        if (!resolveHref && linkNode is not null && !ReferenceEquals(node, linkNode))
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
}
