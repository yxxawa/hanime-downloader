using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Microsoft.Web.WebView2.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Hanime1Downloader.CSharp.Views;

public partial class CloudflareWindow
{

    public async Task<BrowserFetchResult> FetchHtmlAsync(string relativeUrl, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync().WaitAsync(cancellationToken);
        var core = Browser.CoreWebView2 ?? throw new InvalidOperationException("浏览器上下文尚未初始化，请先完成验证。");
        var target = new Uri(SessionBaseUri, relativeUrl);
        if (!_sessionContext.IsTrustedOrigin(target))
            throw new InvalidOperationException("页面请求不属于当前站点。");

        await _fetchLock.WaitAsync(cancellationToken);
        ulong? navigationId = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void HandleNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (navigationId is null && !args.IsRedirected && IsSameDocumentRequest(target, args.Uri))
                navigationId = args.NavigationId;
            else if (navigationId is not null && navigationId != args.NavigationId)
            {
                // 例如验证窗口接管导航。已在等待 DOM 或状态的旧请求都应停止。
                ready.TrySetResult();
                completed.TrySetCanceled();
            }
        }

        void HandleDomReady(object? sender, CoreWebView2DOMContentLoadedEventArgs args)
        {
            if (navigationId == args.NavigationId) ready.TrySetResult();
        }

        void HandleNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (navigationId != args.NavigationId) return;
            completed.TrySetResult(args);
            ready.TrySetResult();
        }

        core.NavigationStarting += HandleNavigationStarting;
        core.DOMContentLoaded += HandleDomReady;
        core.NavigationCompleted += HandleNavigationCompleted;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsActiveVerification())
                throw new CloudflareChallengeException("当前正在进行 Cloudflare 验证，请先完成验证。", target.AbsoluteUri);

            _readyDocumentNavigationId = null;
            var navigationTimeout = TimeSpan.FromSeconds(45);
            core.Navigate(target.AbsoluteUri);
            await ready.Task.WaitAsync(navigationTimeout, cancellationToken);
            if (navigationId is not ulong activeId)
                throw new InvalidOperationException("未能关联当前页面导航。");
            EnsureFetchDocumentIsCurrent(core, activeId, cancellationToken);

            if (completed.Task.IsCompletedSuccessfully && completed.Task.Result is { IsSuccess: false, HttpStatusCode: 0 } failed)
                throw new InvalidOperationException($"页面导航失败: {failed.WebErrorStatus}");

            await WaitForPageContentAsync(core, activeId, target, relativeUrl, cancellationToken);
            var payload = await core.ExecuteScriptAsync(
                "JSON.stringify({ status: performance.getEntriesByType('navigation')[0]?.responseStatus ?? 0, url: location.href, title: document.title, html: document.documentElement?.outerHTML ?? '' })")
                .WaitAsync(cancellationToken);
            EnsureFetchDocumentIsCurrent(core, activeId, cancellationToken);
            var result = DeserializeScriptResult<BrowserFetchResult>(payload) ?? new BrowserFetchResult();
            if (CloudflareDetection.IsChallengePage(result.Html, result.Title))
                throw CloudflareDetection.ConfirmedChallenge(target.AbsoluteUri, new("navigation-dom", Stopwatch.GetTimestamp()));

            var status = result.Status;
            // 旧运行时可能没有 responseStatus。只有这种情况下才等待同一导航的完成事件。
            if (status <= 0 || completed.Task.IsCompletedSuccessfully)
            {
                try
                {
                    var navigation = await completed.Task.WaitAsync(navigationTimeout, cancellationToken);
                    status = navigation.HttpStatusCode > 0 ? navigation.HttpStatusCode : status;
                    if (!navigation.IsSuccess && status <= 0)
                        throw new InvalidOperationException($"页面导航失败: {navigation.WebErrorStatus}");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && completed.Task.IsCanceled)
                {
                    throw new InvalidOperationException("页面导航已切换，已忽略旧请求。");
                }
            }
            EnsureFetchDocumentIsCurrent(core, activeId, cancellationToken);
            if (status == 403)
                throw new CloudflareChallengeException("站点返回 HTTP 403，请检查当前站点会话。", target.AbsoluteUri);

            return new BrowserFetchResult { Status = status, Url = result.Url, Title = result.Title, Html = result.Html };
        }
        finally
        {
            core.NavigationStarting -= HandleNavigationStarting;
            core.DOMContentLoaded -= HandleDomReady;
            core.NavigationCompleted -= HandleNavigationCompleted;
            _fetchLock.Release();
        }
    }

    private void EnsureFetchDocumentIsCurrent(CoreWebView2 core, ulong navigationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_navigationId != navigationId || !Uri.TryCreate(core.Source, UriKind.Absolute, out var source) || !_sessionContext.IsTrustedOrigin(source))
            throw new InvalidOperationException("页面已离开当前请求或当前站点，已忽略旧响应。");
    }

    private static bool IsSameDocumentRequest(Uri target, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var actual) &&
        Uri.Compare(target, actual, UriComponents.HttpRequestUrl, UriFormat.UriEscaped, StringComparison.Ordinal) == 0;

    /// <summary>
    /// 启动时静默复用浏览器里已有的 Cloudflare 会话（窗口保持隐藏）。
    /// 等待跳转后的实际站点页面和对应域的 Cookie 就绪，不能拿旧域 Cookie 提前报告成功。
    /// 托管挑战可能中止首跳并重新加载，因此持续轮询而不把首跳失败当作最终结果。
    /// </summary>
    public async Task<bool> TryReuseSessionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync().WaitAsync(cancellationToken);
        if (Browser.CoreWebView2 is null)
        {
            return false;
        }

        await _fetchLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _navigationPending = true;
            _readyDocumentNavigationId = null;
            Browser.CoreWebView2.Navigate(_siteBaseUrl);
            var reused = await WaitForClearanceAsync(SessionReuseMaxWait, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (reused)
            {
                FinishButton.IsEnabled = true;
                StatusText.Text = $"{_siteHost} 会话已自动恢复，无需手动验证。";
                AppLogger.Info("cloudflare", $"后台会话复用成功，cf_clearance 已就绪，共 {Cookies.Count} 个 Cookie，未弹出验证窗口");
            }

            return reused;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>等待实际站点页面及该域的有效 cf_clearance，而非其他别名的旧 Cookie。</summary>
    private async Task<bool> WaitForClearanceAsync(TimeSpan maxWait, CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Browser.CoreWebView2 is null)
            {
                return false;
            }

            if (await TryRefreshReadySessionAsync(cancellationToken))
            {
                return true;
            }

            if (DateTime.UtcNow - startedAt >= maxWait)
            {
                return false;
            }

            await Task.Delay(ClearancePollInterval, cancellationToken);
        }
    }

    private bool HasValidClearance() => _cookieBridge.HasValidClearance(Cookies);

    /// <summary>只检查浏览器现有 Cookie，不导航、不发送探测请求。</summary>
    public async Task<bool> HasValidSessionCookiesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync().WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Browser.CoreWebView2 is null) return false;
        IsCurrentPageOnSite();
        var cookies = await _cookieBridge.ExportCookiesAsync(Browser.CoreWebView2.CookieManager).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Cookies = cookies;
        CookieHeader = _cookieBridge.BuildCookieHeader(cookies);
        return HasValidClearance();
    }

    private async Task<bool> TryRefreshReadySessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var navigationId = _navigationId;
        // 先捕获 Cookie。图片、广告仍在加载时，也不能把已拿到的 Cookie 显示为未捕获。
        var hasCookies = await HasValidSessionCookiesAsync(cancellationToken);
        _sessionReadinessMessage = hasCookies
            ? "已捕获有效的验证 Cookie，正在等待当前页面准备好。"
            : "尚未捕获当前站点的有效验证 Cookie，请完成页面验证。";
        if (navigationId != _navigationId || _readyDocumentNavigationId != navigationId || !IsCurrentPageOnSite()) return false;
        var payload = await Browser.CoreWebView2.ExecuteScriptAsync(
            "JSON.stringify({ html: document.documentElement?.outerHTML ?? '', title: document.title, ready: document.readyState, href: location.href, status: performance.getEntriesByType('navigation')[0]?.responseStatus ?? 0 })").WaitAsync(cancellationToken);
        var state = DeserializeScriptResult<PageState>(payload);
        if (navigationId != _navigationId || _readyDocumentNavigationId != navigationId || state is null ||
            state.Ready is not ("interactive" or "complete") ||
            !Uri.TryCreate(state.Href, UriKind.Absolute, out var pageUri) ||
            pageUri.GetLeftPart(UriPartial.Authority) != SessionBaseUri.GetLeftPart(UriPartial.Authority)) return false;
        if (CloudflareDetection.IsChallengePage(state.Html ?? string.Empty, state.Title))
        {
            _sessionReadinessMessage = "当前页面仍在进行 Cloudflare 验证，请完成页面中的验证。";
            return false;
        }
        // DOM 就绪时读取当前文档的响应状态，不能因图片未完成而忽略 403。
        var status = _navigationPending ? state.Status : _navigationStatusCode;
        if (status >= 400)
        {
            _sessionReadinessMessage = hasCookies
                ? $"已捕获验证 Cookie，但当前页面仍返回 HTTP {status}，请重试页面验证。"
                : $"当前页面返回 HTTP {status}，尚未捕获有效验证 Cookie。";
            return false;
        }
        if (status is < 200 or >= 300)
        {
            _sessionReadinessMessage = hasCookies
                ? "已捕获验证 Cookie，正在确认当前页面的响应状态。"
                : "正在确认当前页面和验证 Cookie。";
            return false;
        }
        if (!hasCookies)
            _sessionReadinessMessage = "页面已加载，尚未捕获当前站点的有效验证 Cookie，正在继续检查。";
        return hasCookies;
    }

    /// <summary>
    /// 在当前 WebView2 页面上下文里用 fetch() 抓取页面 HTML。
    /// 无需导航或渲染 DOM；遇到明确的挑战响应头时只回传标记，不等待响应体。
    /// 返回 null 表示当前上下文不可用，由调用方选择回退路径。
    /// </summary>
    public async Task<BrowserFetchResult?> TryFetchHtmlInPageAsync(string relativeUrl, CancellationToken cancellationToken = default, bool verifyRequest = false)
    {
        await EnsureInitializedAsync().WaitAsync(cancellationToken);
        if (Browser.CoreWebView2 is null || !IsCurrentPageOnSite())
        {
            return null;
        }

        var target = new Uri(SessionBaseUri, relativeUrl);
        if (!_sessionContext.IsTrustedOrigin(target)) return null;
        var navigationId = _navigationId;
        var targetUrl = target.AbsoluteUri;
        var urlLiteral = JsonSerializer.Serialize(targetUrl);
        var slot = "__hanimeFetch_" + Guid.NewGuid().ToString("N");
        var slotLiteral = JsonSerializer.Serialize(slot);
        // postMessage 只做一次 JSON 编码；旧的 JSON.stringify + ExecuteScriptAsync 会把整页 HTML 编码两遍。
        var cacheOption = verifyRequest ? ",cache:'no-store'" : string.Empty;
        var readBody = verifyRequest
            ? "let t='';const ct=(r.headers.get('content-type')||'').toLowerCase();if(ct.includes('text/html')||ct.includes('application/xhtml+xml')){t=await r.text();}else{await r.body?.cancel();}"
            : "const t=await r.text();";
        var script =
            "(function(){(async function(){try{const r=await fetch(" + urlLiteral + ",{credentials:'include'" + cacheOption + ",headers:{'X-Requested-With':'XMLHttpRequest'}});" +
            "const mitigation=r.headers.get('" + CloudflareDetection.ChallengeHeaderName + "');" +
            "if(mitigation?.trim().toLowerCase()==='challenge'){chrome.webview.postMessage({slot:" + slotLiteral + ",status:r.status,url:r.url,mitigation});void r.body?.cancel().catch(()=>{});return;}" +
            readBody + "chrome.webview.postMessage({slot:" + slotLiteral + ",status:r.status,url:r.url,html:t});}" +
            "catch(e){chrome.webview.postMessage({slot:" + slotLiteral + ",status:-1,url:'',html:'',error:String(e)});}})();})();";

        var completion = new TaskCompletionSource<ScriptFetchPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        _scriptFetchWaiters[slot] = completion;
        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync(script).WaitAsync(cancellationToken);
            var fetched = await completion.Task.WaitAsync(ScriptFetchTimeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (fetched.Status <= 0)
            {
                AppLogger.Info("http", $"页内 fetch 失败: {relativeUrl} {fetched.Error}");
                return null;
            }

            if (CloudflareDetection.IsChallengeHeader(fetched.Mitigation))
            {
                // 只接受发起此请求的同源文档及受信任站点的响应；丢弃旧页面迟到的回传。
                if (navigationId != _navigationId || !Uri.TryCreate(fetched.MessageSource, UriKind.Absolute, out var source) ||
                    !source.GetLeftPart(UriPartial.Authority).Equals(target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(fetched.Url, UriKind.Absolute, out var effective) || !_sessionContext.IsTrustedOrigin(effective))
                    return null;

                return new BrowserFetchResult
                {
                    Status = fetched.Status, Url = effective.AbsoluteUri,
                    Challenge = new("browser-fetch-headers", Stopwatch.GetTimestamp(), fetched.ReceivedAt)
                };
            }

            return new BrowserFetchResult
            {
                Status = fetched.Status,
                Url = string.IsNullOrWhiteSpace(fetched.Url) ? targetUrl : fetched.Url,
                Html = fetched.Html ?? string.Empty,
                Title = ExtractTitleFromHtml(fetched.Html)
            };
        }
        catch (TimeoutException)
        {
            AppLogger.Info("http", $"页内 fetch 超时: {relativeUrl}");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Info("http", $"页内 fetch 注入失败: {relativeUrl} {ex.Message}");
            return null;
        }
        finally
        {
            _scriptFetchWaiters.TryRemove(slot, out _);
        }
    }

    /// <summary>
    /// 在当前 WebView2 页面上下文里提交 application/x-www-form-urlencoded 表单，并返回响应 HTML。
    /// 浏览器会自动处理登录响应中的 Set-Cookie。
    /// </summary>
    public async Task<BrowserFetchResult?> PostFormInPageAsync(
        string relativeUrl,
        IReadOnlyDictionary<string, string> formFields,
        string? csrfToken = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();
        if (Browser.CoreWebView2 is null || !IsCurrentPageOnSite())
        {
            return null;
        }

        var targetUrl = new Uri(new Uri(_siteBaseUrl), relativeUrl).ToString();
        var urlLiteral = JsonSerializer.Serialize(targetUrl);
        var fieldsLiteral = JsonSerializer.Serialize(formFields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        var tokenLiteral = JsonSerializer.Serialize(csrfToken ?? string.Empty);
        var slot = "__hanimePost_" + Guid.NewGuid().ToString("N");
        var slotLiteral = JsonSerializer.Serialize(slot);
        var script =
            "(function(){(async function(){try{const p=new URLSearchParams(" + fieldsLiteral + ");" +
            "const r=await fetch(" + urlLiteral + ",{method:'POST',credentials:'include',redirect:'follow'," +
            "headers:{'Content-Type':'application/x-www-form-urlencoded;charset=UTF-8','X-Requested-With':'XMLHttpRequest','X-CSRF-TOKEN':" + tokenLiteral + "}," +
            "body:p.toString()});const t=await r.text();chrome.webview.postMessage({slot:" + slotLiteral + ",status:r.status,url:r.url,html:t});}" +
            "catch(e){chrome.webview.postMessage({slot:" + slotLiteral + ",status:-1,url:'',html:'',error:String(e)});}})();})();";

        var completion = new TaskCompletionSource<ScriptFetchPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        _scriptFetchWaiters[slot] = completion;
        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync(script);
            var fetched = await completion.Task.WaitAsync(ScriptFetchTimeout, cancellationToken);
            if (fetched.Status <= 0)
            {
                AppLogger.Info("http", $"页内 POST 失败: {relativeUrl} {fetched.Error}");
                return null;
            }

            return new BrowserFetchResult
            {
                Status = fetched.Status,
                Url = string.IsNullOrWhiteSpace(fetched.Url) ? targetUrl : fetched.Url,
                Html = fetched.Html ?? string.Empty,
                Title = ExtractTitleFromHtml(fetched.Html)
            };
        }
        catch (TimeoutException)
        {
            AppLogger.Info("http", $"页内 POST 超时: {relativeUrl}");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Info("http", $"页内 POST 注入失败: {relativeUrl} {ex.Message}");
            return null;
        }
        finally
        {
            _scriptFetchWaiters.TryRemove(slot, out _);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var receivedAt = Stopwatch.GetTimestamp();
            var payload = JsonSerializer.Deserialize<ScriptFetchPayload>(e.WebMessageAsJson, ScriptJsonOptions);
            if (payload?.Slot is { Length: > 0 } slot && _scriptFetchWaiters.TryRemove(slot, out var completion))
            {
                payload.ReceivedAt = receivedAt;
                payload.MessageSource = e.Source;
                completion.TrySetResult(payload);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Info("http", $"页内 fetch 回传解析失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 页内 fetch 不可用时（例如 WebView2 当前页已被导航到别处），把页面导航回站点首页以恢复上下文。
    /// </summary>
    public async Task<bool> TryRecoverPageContextAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync().WaitAsync(cancellationToken);
        if (Browser.CoreWebView2 is null || IsActiveVerification())
        {
            return false;
        }

        if (IsCurrentPageOnSite())
        {
            return true;
        }

        await _fetchLock.WaitAsync(cancellationToken);
        try
        {
            if (IsActiveVerification()) return false;
            AppLogger.Info("http", $"WebView2 页面不在站点上，导航回首页恢复页内 fetch 上下文: {Browser.CoreWebView2.Source}");
            _readyDocumentNavigationId = null;
            Browser.CoreWebView2.Navigate(_siteBaseUrl);
            var startedAt = DateTime.UtcNow;
            while (DateTime.UtcNow - startedAt < SessionReuseMaxWait)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsCurrentPageOnSite())
                {
                    return true;
                }

                await Task.Delay(ClearancePollInterval, cancellationToken);
            }

            return IsCurrentPageOnSite();
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private bool IsCurrentPageOnSite()
    {
        if (Browser.CoreWebView2 is null || !Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return _sessionContext.TryUpdateFromNavigation(uri.AbsoluteUri);
    }

    private static string ExtractTitleFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "<title\\b[^>]*>(.*?)</title>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : string.Empty;
    }
}
