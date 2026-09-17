using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Views;
using HtmlAgilityPack;
using System.Text.RegularExpressions;

namespace Hanime1Downloader.CSharp.Services;

public sealed record HanimeAccountIdentity(string UserId, string UserName, string Email);

public sealed class HanimeAccountService
{
    private static readonly Regex UserIdRegex = new(@"/user/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VideoIdRegex = new(@"(?:[?&](?:v|id|video[_-]?id|videoId)=|/(?:watch|video|videos)(?:/|=))(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SkipPageMaxRegex = new(@"validateNumberInput\(this,\s*\d+,\s*(\d+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PageQueryRegex = new(@"[?&]page=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly CloudflareWindow _browserWindow;
    private readonly string _siteBase;
    private readonly Uri _siteBaseUri;
    private string _csrfToken = string.Empty;

    public HanimeAccountService(CloudflareWindow browserWindow, string siteHost)
    {
        _browserWindow = browserWindow;
        _siteBase = $"https://{siteHost}";
        _siteBaseUri = new Uri($"{_siteBase}/");
    }

    public async Task<HanimeAccountIdentity> EnsureLoggedInAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("账号邮箱不能为空。");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            var current = await GetCurrentAccountAsync(cancellationToken)
                ?? throw new InvalidOperationException("账号登录状态已失效，请在设置中重新输入密码登录。");
            return await ValidateAccountEmailAsync(current, email, cancellationToken);
        }

        var loginPage = await GetHtmlAsync("login", cancellationToken);
        var token = ExtractHiddenToken(loginPage.Html);
        if (string.IsNullOrWhiteSpace(token))
        {
            var alreadyLoggedIn = await GetCurrentAccountAsync(cancellationToken);
            if (alreadyLoggedIn is not null)
            {
                return await ValidateAccountEmailAsync(alreadyLoggedIn, email, cancellationToken);
            }

            throw new InvalidOperationException("登录页中未找到 _token，可能被 Cloudflare 或站点改版拦截。");
        }

        _csrfToken = token;
        var postResult = await _browserWindow.PostFormInPageAsync(
            "login",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["_token"] = token,
                ["email"] = email.Trim(),
                ["password"] = password
            },
            token,
            cancellationToken);

        if (postResult is null)
        {
            throw new InvalidOperationException("登录请求未能通过浏览器会话提交，请先完成站点验证后重试。");
        }

        if (postResult.Status >= 400)
        {
            throw new InvalidOperationException($"登录请求失败，HTTP {postResult.Status}。");
        }

        var identity = await GetCurrentAccountAsync(cancellationToken);
        if (identity is not null)
        {
            return await ValidateAccountEmailAsync(identity, email, cancellationToken);
        }

        var message = ExtractFormError(postResult.Html);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = ExtractFormError(loginPage.Html);
        }
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? "账号或密码错误，未能确认登录状态。"
            : message);
    }

    private async Task<HanimeAccountIdentity> ValidateAccountEmailAsync(
        HanimeAccountIdentity identity,
        string expectedEmail,
        CancellationToken cancellationToken)
    {
        var accountPage = await GetHtmlAsync($"user/{identity.UserId}/edit", cancellationToken);
        var doc = new HtmlDocument();
        doc.LoadHtml(accountPage.Html);
        var actualEmail = doc.DocumentNode.SelectSingleNode("//input[@name='email']")?.GetAttributeValue("value", string.Empty)?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(actualEmail) &&
            !actualEmail.Equals(expectedEmail.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Info("favorites-account", $"账号邮箱已按站点实际值校正: settings={expectedEmail.Trim()}, actual={actualEmail}");
            return identity with { Email = actualEmail };
        }

        return string.IsNullOrWhiteSpace(actualEmail) ? identity : identity with { Email = actualEmail };
    }

    public async Task<HanimeAccountIdentity?> GetCurrentAccountAsync(CancellationToken cancellationToken = default)
    {
        var home = await GetHtmlAsync(string.Empty, cancellationToken);
        return ParseIdentity(home.Html);
    }

    public async Task<IReadOnlyList<VideoSummary>> FetchFavoriteVideosAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException("缺少账号用户 ID，无法读取收藏夹。");
        }

        var videos = new List<VideoSummary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxPage = 1;
        for (var page = 1; page <= maxPage && page <= 100; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await GetHtmlAsync($"user/{userId}/likes?page={page}", cancellationToken);
            if (response.Status >= 400)
            {
                throw new InvalidOperationException($"读取账号收藏夹失败，HTTP {response.Status}。");
            }

            var pageToken = ExtractHiddenToken(response.Html);
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                _csrfToken = pageToken;
            }

            if (page == 1)
            {
                maxPage = ParseMaxPage(response.Html);
            }

            var pageItems = ParseFavoriteVideos(response.Html);
            if (pageItems.Count == 0)
            {
                break;
            }

            foreach (var video in pageItems)
            {
                if (seen.Add(video.VideoId))
                {
                    videos.Add(video);
                }
            }
        }

        return videos;
    }

    public async Task<bool> SetFavoriteAsync(
        string videoId,
        bool shouldBeFavorite,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException("收藏视频缺少视频 ID 或账号用户 ID。");
        }

        var token = await GetCsrfTokenAsync(cancellationToken);

        // Han1meViewer 的接口约定：like-status=1 表示取消收藏，空字符串表示收藏。
        var result = await _browserWindow.PostFormInPageAsync(
            "like",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["like-foreign-id"] = videoId,
                ["like-status"] = shouldBeFavorite ? string.Empty : "1",
                ["_token"] = token,
                ["like-user-id"] = userId,
                ["like-is-positive"] = "1"
            },
            token,
            cancellationToken);

        if (result is null)
        {
            throw new InvalidOperationException("收藏接口请求未能通过浏览器会话提交。");
        }

        if (CloudflareDetection.IsChallengePage(result.Html, result.Title))
        {
            throw new InvalidOperationException("收藏接口被 Cloudflare 挑战页拦截，请先完成站点验证。");
        }

        if (Uri.TryCreate(result.Url, UriKind.Absolute, out var finalUri) &&
            finalUri.AbsolutePath.EndsWith("/login", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("账号登录状态已失效，请重新绑定账号。");
        }

        if (result.Status >= 200 && result.Status < 300)
        {
            // Han1meViewer 只判断 HTTP 成功，不要求响应体为 {"success":true}。
            AppLogger.Info("favorites-account", $"收藏接口成功: video={videoId}, favorite={shouldBeFavorite}, status={result.Status}");
            return true;
        }

        if (result.Status is 401 or 403 or 419)
        {
            _csrfToken = string.Empty;
        }

        throw new InvalidOperationException($"收藏接口请求失败，HTTP {result.Status}。");
    }

    private async Task<string> GetCsrfTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_csrfToken))
        {
            return _csrfToken;
        }

        var home = await GetHtmlAsync(string.Empty, cancellationToken);
        _csrfToken = ExtractHiddenToken(home.Html);
        if (string.IsNullOrWhiteSpace(_csrfToken))
        {
            throw new InvalidOperationException("未能获取账号 CSRF Token，请重新绑定账号。");
        }

        return _csrfToken;
    }
    private async Task<BrowserFetchResult> GetHtmlAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        var inPage = await _browserWindow.TryFetchHtmlInPageAsync(relativeUrl, cancellationToken);
        if (inPage is not null)
        {
            return ValidateHtml(inPage);
        }

        if (await _browserWindow.TryRecoverPageContextAsync(cancellationToken))
        {
            inPage = await _browserWindow.TryFetchHtmlInPageAsync(relativeUrl, cancellationToken);
            if (inPage is not null)
            {
                return ValidateHtml(inPage);
            }
        }

        return ValidateHtml(await _browserWindow.FetchHtmlAsync(relativeUrl, cancellationToken));
    }

    private static BrowserFetchResult ValidateHtml(BrowserFetchResult response)
    {
        if (CloudflareDetection.IsChallengePage(response.Html, response.Title))
        {
            throw new InvalidOperationException("请求被 Cloudflare 挑战页拦截，请先完成站点验证后重试。");
        }

        return response;
    }

    private List<VideoSummary> ParseFavoriteVideos(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var wrappers = doc.DocumentNode.SelectNodes("//div[starts-with(@class, 'user-tab-item-wrapper')]")?.ToList() ?? [];
        var videos = new List<VideoSummary>(wrappers.Count);

        foreach (var wrapper in wrappers)
        {
            var linkNode = wrapper.SelectSingleNode(".//a[@href or @data-href or @data-url]")
                ?? wrapper.SelectSingleNode(".//*[@data-video-id or @data-id]");
            var href = ReadLinkValue(linkNode ?? wrapper);
            if (!TryExtractVideoId(linkNode, href, out var videoId))
            {
                continue;
            }

            var coverNode = wrapper.SelectSingleNode(".//img[@src or @data-src or @data-original or @data-lazy-src]")
                ?? linkNode?.SelectSingleNode(".//img[@src or @data-src or @data-original or @data-lazy-src]");
            var title = ExtractTitle(wrapper, videoId);
            videos.Add(new VideoSummary
            {
                VideoId = videoId,
                Title = title,
                Url = $"{_siteBase}/watch?v={videoId}",
                CoverUrl = ExtractCoverUrl(coverNode)
            });
        }

        return videos;
    }

    private string ExtractTitle(HtmlNode wrapper, string videoId)
    {
        foreach (var selector in new[]
        {
            ".//div[contains(@class, 'title')]",
            ".//h4[contains(@class, 'video-title')]",
            ".//h1",
            ".//h2",
            ".//h3",
            ".//h4"
        })
        {
            var title = ToDisplayText(wrapper.SelectSingleNode(selector)?.InnerText);
            if (IsUsableTitle(title, videoId))
            {
                return title;
            }
        }

        foreach (var node in wrapper.SelectNodes(".//*[@data-title or @data-name or @title or @aria-label or @alt]")?.ToList() ?? [])
        {
            foreach (var attribute in new[] { "data-title", "data-name", "title", "aria-label", "alt" })
            {
                var title = ToDisplayText(node.GetAttributeValue(attribute, string.Empty));
                if (IsUsableTitle(title, videoId))
                {
                    return title;
                }
            }
        }

        return $"视频 {videoId}";
    }

    private string ExtractCoverUrl(HtmlNode? coverNode)
    {
        if (coverNode is null)
        {
            return string.Empty;
        }

        foreach (var attribute in new[] { "src", "data-src", "data-original", "data-lazy-src" })
        {
            var value = HtmlEntity.DeEntitize(coverNode.GetAttributeValue(attribute, string.Empty)).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + value;
            }

            return Uri.TryCreate(_siteBaseUri, value, out var absolute) ? absolute.ToString() : value;
        }

        return string.Empty;
    }

    private static bool TryExtractVideoId(HtmlNode? linkNode, string href, out string videoId)
    {
        foreach (var attribute in new[] { "data-video-id", "data-video", "video-id", "data-id", "data-v" })
        {
            var value = linkNode?.GetAttributeValue(attribute, string.Empty);
            if (!string.IsNullOrWhiteSpace(value) && value.All(char.IsDigit))
            {
                videoId = value;
                return true;
            }
        }

        var match = VideoIdRegex.Match(HtmlEntity.DeEntitize(href ?? string.Empty));
        if (match.Success)
        {
            videoId = match.Groups[1].Value;
            return true;
        }

        videoId = string.Empty;
        return false;
    }

    private static string ReadLinkValue(HtmlNode node)
    {
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

    private static string ExtractHiddenToken(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var input = doc.DocumentNode.SelectSingleNode("//input[@name='_token']");
        if (input is not null)
        {
            return input.GetAttributeValue("value", string.Empty)?.Trim() ?? string.Empty;
        }

        return doc.DocumentNode.SelectSingleNode("//meta[@name='csrf-token']")?.GetAttributeValue("content", string.Empty)?.Trim() ?? string.Empty;
    }

    private static HanimeAccountIdentity? ParseIdentity(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var trigger = doc.DocumentNode.SelectSingleNode("//*[@id='user-modal-trigger']");
        var match = UserIdRegex.Match(trigger?.GetAttributeValue("href", string.Empty) ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        var userName = ToDisplayText(doc.DocumentNode.SelectSingleNode("//*[@id='user-modal-name']")?.InnerText);
        return new HanimeAccountIdentity(match.Groups[1].Value, string.IsNullOrWhiteSpace(userName) ? match.Groups[1].Value : userName, string.Empty);
    }

    private static string ExtractFormError(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        foreach (var selector in new[] { "//*[contains(@class,'alert-danger')]", "//*[contains(@class,'invalid-feedback')]", "//*[contains(@class,'text-danger')]", "//*[contains(@class,'help-block')]", "//*[contains(@class,'error-message')]" })
        {
            var text = ToDisplayText(doc.DocumentNode.SelectSingleNode(selector)?.InnerText);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static int ParseMaxPage(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var skipInput = doc.DocumentNode.SelectSingleNode("//input[@id='skip-page-input']");
        var skipMatch = SkipPageMaxRegex.Match(skipInput?.GetAttributeValue("oninput", string.Empty) ?? string.Empty);
        if (skipMatch.Success && int.TryParse(skipMatch.Groups[1].Value, out var skipMax))
        {
            return Math.Max(1, skipMax);
        }

        var maxPage = 1;
        foreach (var link in doc.DocumentNode.SelectNodes("//ul[contains(@class,'pagination')]//a[contains(@class,'page-link')]")?.ToList() ?? [])
        {
            var match = PageQueryRegex.Match(link.GetAttributeValue("href", string.Empty));
            if (match.Success && int.TryParse(match.Groups[1].Value, out var page))
            {
                maxPage = Math.Max(maxPage, page);
            }
        }

        return maxPage;
    }

    private static bool IsUsableTitle(string title, string videoId)
    {
        var normalized = ToDisplayText(title);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        return !compact.Equals(videoId, StringComparison.OrdinalIgnoreCase) &&
               !compact.Equals($"视频{videoId}", StringComparison.OrdinalIgnoreCase) &&
               !compact.Equals($"video{videoId}", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToDisplayText(string? value)
    {
        var decoded = HtmlEntity.DeEntitize(value ?? string.Empty).Trim();
        return SimplifiedChineseConverter.ToSimplified(string.Join(" ", decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
    }
}