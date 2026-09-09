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
    /// <summary>HTML 缓存条数上限：每条是整页 HTML（100-400KB），必须限制，否则长时间浏览会持续吃内存。</summary>
    private const int HtmlCacheMaxEntries = 200;
    private readonly ConcurrentDictionary<string, Lazy<Task<BrowserFetchResult>>> _htmlInFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DirectHtmlTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DirectHttpCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DirectHttpRetryDelay = TimeSpan.FromMilliseconds(300);
    /// <summary>用户请求：只快速试 2 次（约 0.6 秒），失败立刻走页内 fetch，不让用户等重试。</summary>
    private const int DirectHttpMaxAttempts = 2;
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

    public async Task<VideoDetails?> GetDetailsAsync(string videoId, VideoDetailsLoadOptions loadOptions = VideoDetailsLoadOptions.Basic | VideoDetailsLoadOptions.Sources, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        var needsRichContent = loadOptions.HasFlag(VideoDetailsLoadOptions.RelatedVideos) ||
                               loadOptions.HasFlag(VideoDetailsLoadOptions.Meta) ||
                               loadOptions.HasFlag(VideoDetailsLoadOptions.Tags);
        var needsSources = loadOptions.HasFlag(VideoDetailsLoadOptions.Sources);
        var needsCover = loadOptions.HasFlag(VideoDetailsLoadOptions.Cover);

        if (!needsRichContent && (needsSources || needsCover))
        {
            // 轻量路径：download 页（~30KB）就包含全部清晰度源 + 标题 + 封面，
            // 只有需要相关视频/简介/标签时才必须加载 ~148KB 的 watch 页。
            var parsed = await TryLoadFromDownloadPageAsync(videoId, loadOptions, cancellationToken, forceRefresh);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        var watchResponse = await FetchHtmlAsync($"watch?v={videoId}", cancellationToken, forceRefresh);
        EnsureNotBlocked(watchResponse);

        BrowserFetchResult? downloadResponse = null;
        Task<BrowserFetchResult>? downloadTask = null;
        if (needsSources && !HasEmbeddedSourceHint(watchResponse.Html))
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
        if (needsSources && parsedDetails.Sources.Count == 0)
        {
            downloadResponse = downloadTask is not null
                ? await downloadTask
                : await FetchHtmlAsync($"download?v={videoId}", cancellationToken);
            EnsureNotBlocked(downloadResponse);
            parsedDetails = await Task.Run(() => MergeDownloadSources(parsedDetails, downloadResponse.Html), cancellationToken);
        }

        if (needsSources)
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

    private async Task<BrowserFetchResult> FetchHtmlAsync(string relativeUrl, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targetUri = new Uri(_siteBaseUri, relativeUrl);
        var cacheKey = targetUri.AbsoluteUri;
        var now = DateTimeOffset.UtcNow;
        if (!forceRefresh && _htmlCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
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
                StoreHtmlCacheEntry(cacheKey, new HtmlCacheEntry(response, DateTimeOffset.UtcNow + GetCacheDuration(relativeUrl)));
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
        // 首选：WebView2 页内 fetch。用真实 Chromium 的 TLS/HTTP2 指纹 + 同源 Cookie 取 HTML，
        // 不会被 Cloudflare 挑战，也不渲染 DOM（实测单页 300-600ms，可并发）。
        // 直连 .NET HTTP 在代理出口为机房 IP 时会被 Cloudflare 持续挑战，因此降为次选。
        var pageFetchWatch = Stopwatch.StartNew();
        try
        {
            var inPage = await _browserWindow.TryFetchHtmlInPageAsync(relativeUrl, cancellationToken);
            if (inPage is not null)
            {
                AppLogger.InfoThrottled("http", $"页内 fetch 成功: {relativeUrl} status={inPage.Status} bytes={inPage.Html.Length} ms={pageFetchWatch.ElapsedMilliseconds}", TimeSpan.FromSeconds(3));
                Debug.WriteLine($"[html-pagefetch] {targetUri} status={inPage.Status} bytes={inPage.Html.Length} ms={pageFetchWatch.ElapsedMilliseconds}");
                return inPage;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[html-pagefetch] failed: {targetUri} {ex.Message}");
        }

        if (_httpClient is not null && IsDirectHttpAvailable())
        {
            var direct = await TryFetchViaHttpAsync(targetUri, cancellationToken, DirectHttpMaxAttempts);
            if (direct is not null)
            {
                AppLogger.InfoThrottled("http", $"直连 HTTP 成功: {relativeUrl}", TimeSpan.FromSeconds(3));
                return direct;
            }
        }

        // 页内 fetch 不可用（例如 WebView2 页面被导航走）：先把它导航回站点首页恢复上下文，再重试一次，
        // 避免直接退化成被单锁串行化的整页导航（实测单页 3.7-4.9 秒）。
        try
        {
            if (await _browserWindow.TryRecoverPageContextAsync(cancellationToken))
            {
                var recovered = await _browserWindow.TryFetchHtmlInPageAsync(relativeUrl, cancellationToken);
                if (recovered is not null)
                {
                    AppLogger.InfoThrottled("http", $"恢复页面上下文后页内 fetch 成功: {relativeUrl}", TimeSpan.FromSeconds(3));
                    return recovered;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[html-pagecontext] recover failed: {ex.Message}");
        }

        AppLogger.InfoThrottled("http", $"页内 fetch 不可用 -> 回落整页导航: {relativeUrl}", TimeSpan.FromSeconds(3));
        return await _browserWindow.FetchHtmlAsync(relativeUrl, cancellationToken);
    }

    /// <summary>
    /// 直连 HTTP 抓取。Cloudflare 对新建连接的前 1-2 个请求必发托管挑战
    /// （.NET 的 TLS 指纹与真实 Chromium 不同 + 代理出口被判定为机房），
    /// 实测约 1.2-1.8 秒后同一连接即全部放行，因此这里按时间窗口重试；
    /// 只有连续失败才进入冷却，避免一次瞬时挑战把后续请求全部退化成 WebView2 整页导航。
    /// </summary>
    private async Task<BrowserFetchResult?> TryFetchViaHttpAsync(Uri targetUri, CancellationToken cancellationToken, int maxAttempts)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                using var response = await _httpClient!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
                var html = await response.Content.ReadAsStringAsync(requestTimeout.Token);
                var result = new BrowserFetchResult
                {
                    Status = (int)response.StatusCode,
                    Url = response.RequestMessage?.RequestUri?.ToString() ?? targetUri.ToString(),
                    Html = html,
                    Title = ExtractHtmlTitle(html)
                };

                if (response.IsSuccessStatusCode && !CloudflareDetection.IsChallengePage(html, result.Title))
                {
                    if (attempt > 1)
                    {
                        AppLogger.Info("http", $"直连 HTTP 第 {attempt} 次尝试成功（前 {attempt - 1} 次被 Cloudflare 挑战）: {targetUri}");
                    }

                    Debug.WriteLine($"[html-http] {targetUri} status={(int)response.StatusCode} bytes={html.Length} attempt={attempt}");
                    return result;
                }

                Debug.WriteLine($"[html-http] attempt {attempt}/{maxAttempts} blocked: {targetUri} status={(int)response.StatusCode} challenge={CloudflareDetection.IsChallengePage(html, result.Title)}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Debug.WriteLine($"[html-http] attempt {attempt}/{maxAttempts} timeout: {targetUri}");
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine($"[html-http] attempt {attempt}/{maxAttempts} failed: {targetUri} error={ex.Message}");
            }
            catch (ObjectDisposedException)
            {
                DisableDirectHttp();
                Debug.WriteLine($"[html-http] client disposed, fallback to WebView2: {targetUri}");
                return null;
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(DirectHttpRetryDelay, cancellationToken);
            }
        }

        DisableDirectHttp();
        Debug.WriteLine($"[html-http] direct HTTP disabled for {DirectHttpCooldown.TotalSeconds:0}s after {maxAttempts} attempts: {targetUri}");
        return null;
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
               !CloudflareDetection.IsChallengePage(response.Html, response.Title);
    }

    /// <summary>写入 HTML 缓存并做容量控制：先清理过期项，仍超上限时按最早过期时间淘汰。</summary>
    private void StoreHtmlCacheEntry(string cacheKey, HtmlCacheEntry entry)
    {
        _htmlCache[cacheKey] = entry;
        if (_htmlCache.Count <= HtmlCacheMaxEntries)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _htmlCache)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _htmlCache.TryRemove(pair.Key, out _);
            }
        }

        if (_htmlCache.Count <= HtmlCacheMaxEntries)
        {
            return;
        }

        foreach (var key in _htmlCache
                     .OrderBy(pair => pair.Value.ExpiresAt)
                     .Take(_htmlCache.Count - HtmlCacheMaxEntries)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _htmlCache.TryRemove(key, out _);
        }
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

    private sealed record HtmlCacheEntry(BrowserFetchResult Response, DateTimeOffset ExpiresAt);

    private static void EnsureNotBlocked(BrowserFetchResult response)
    {
        var marker = CloudflareDetection.FindChallengeMarker(response.Html, response.Title);
        if (marker is not null)
        {
            var snippet = CloudflareDetection.BuildContextSnippet(response.Html, marker);
            Debug.WriteLine($"[cf-block] marker={marker} url={response.Url} context={snippet}");
            AppLogger.Info("cloudflare", $"检测到挑战页: marker={marker}, url={response.Url}, context={snippet}");
            throw new InvalidOperationException($"请求被 Cloudflare 挑战页拦截（标记: {marker}），请重新验证。页面仍是 Cloudflare 验证页。");
        }

        if (response.Status == 403)
        {
            throw new InvalidOperationException("站点返回 403（疑似 Cloudflare 拦截），当前浏览器会话未被接受。请在验证窗口中先确认主页已正常打开。");
        }
    }
}
