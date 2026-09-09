using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Microsoft.Web.WebView2.Core;
using System.Collections.Concurrent;
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
        await EnsureInitializedAsync();
        if (Browser.CoreWebView2 is null)
        {
            throw new InvalidOperationException("浏览器上下文尚未初始化，请先完成验证。");
        }

        var targetUrl = new Uri(new Uri(_siteBaseUrl), relativeUrl).ToString();
        await _fetchLock.WaitAsync(cancellationToken);
        var navigationCompletionSource = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void HandleNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            navigationCompletionSource.TrySetResult(args);
        }

        Browser.CoreWebView2.NavigationCompleted += HandleNavigationCompleted;
        try
        {
            Browser.CoreWebView2.Navigate(targetUrl);
            var navigation = await navigationCompletionSource.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);

            await WaitForPageContentAsync(relativeUrl, cancellationToken);
            var payload = await Browser.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({ status: document.documentElement ? 200 : 0, url: location.href, title: document.title, html: document.documentElement ? document.documentElement.outerHTML : '' })");
            var result = DeserializeScriptResult<BrowserFetchResult>(payload) ?? new BrowserFetchResult();

            if (!navigation.IsSuccess &&
                navigation.WebErrorStatus != CoreWebView2WebErrorStatus.Unknown &&
                string.IsNullOrWhiteSpace(result.Html))
            {
                throw new InvalidOperationException($"页面导航失败: {navigation.WebErrorStatus}");
            }

            return result;
        }
        finally
        {
            Browser.CoreWebView2.NavigationCompleted -= HandleNavigationCompleted;
            _fetchLock.Release();
        }
    }

    /// <summary>
    /// 启动时静默复用浏览器里已有的 Cloudflare 会话（窗口保持隐藏）。
    /// 不再依赖 NavigationCompleted：托管挑战通过后页面会 location.reload()，
    /// 首跳通常以 ConnectionAborted 结束，但 cf_clearance 其实已经拿到。
    /// </summary>
    public async Task<bool> TryReuseSessionAsync()
    {
        await EnsureInitializedAsync();
        if (Browser.CoreWebView2 is null)
        {
            return false;
        }

        await _fetchLock.WaitAsync();
        try
        {
            Browser.CoreWebView2.Navigate(_siteBaseUrl);
            var reused = await WaitForClearanceAsync(SessionReuseMaxWait, CancellationToken.None);
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

    /// <summary>轮询 Cookie 直到拿到未过期的 cf_clearance（不依赖页面导航状态）。</summary>
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

            Cookies = await _cookieBridge.ExportCookiesAsync(Browser.CoreWebView2.CookieManager);
            CookieHeader = _cookieBridge.BuildCookieHeader(Cookies);
            if (HasValidClearance())
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

    private bool HasValidClearance()
    {
        var nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Cookies.Any(cookie =>
            cookie.Name.Equals("cf_clearance", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cookie.Value) &&
            (cookie.Expires is not { } expires || expires <= 0 || expires > nowUnixSeconds + 30));
    }

    /// <summary>
    /// 在当前 WebView2 页面上下文里用 fetch() 抓取页面 HTML。
    /// 与整页导航相比：使用真实 Chromium 的 TLS/HTTP2 指纹与同源 Cookie，不会被 Cloudflare 挑战，
    /// 也不需要渲染 DOM（实测单页 200-320ms，可并发）。返回 null 表示当前上下文不可用，由调用方回落导航。
    /// </summary>
    public async Task<BrowserFetchResult?> TryFetchHtmlInPageAsync(string relativeUrl, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();
        if (Browser.CoreWebView2 is null || !IsCurrentPageOnSite())
        {
            return null;
        }

        var targetUrl = new Uri(new Uri(_siteBaseUrl), relativeUrl).ToString();
        var urlLiteral = JsonSerializer.Serialize(targetUrl);
        var slot = "__hanimeFetch_" + Guid.NewGuid().ToString("N");
        var slotLiteral = JsonSerializer.Serialize(slot);
        // postMessage 只做一次 JSON 编码；旧的 JSON.stringify + ExecuteScriptAsync 会把整页 HTML 编码两遍。
        var script =
            "(function(){(async function(){try{const r=await fetch(" + urlLiteral + ",{credentials:'include',headers:{'X-Requested-With':'XMLHttpRequest'}});" +
            "const t=await r.text();chrome.webview.postMessage({slot:" + slotLiteral + ",status:r.status,url:r.url,html:t});}" +
            "catch(e){chrome.webview.postMessage({slot:" + slotLiteral + ",status:-1,url:'',html:'',error:String(e)});}})();})();";

        var completion = new TaskCompletionSource<ScriptFetchPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        _scriptFetchWaiters[slot] = completion;
        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync(script);
            var fetched = await completion.Task.WaitAsync(ScriptFetchTimeout, cancellationToken);
            if (fetched.Status <= 0)
            {
                AppLogger.Info("http", $"页内 fetch 失败: {relativeUrl} {fetched.Error}");
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

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<ScriptFetchPayload>(e.WebMessageAsJson, ScriptJsonOptions);
            if (payload?.Slot is { Length: > 0 } slot && _scriptFetchWaiters.TryRemove(slot, out var completion))
            {
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
        await EnsureInitializedAsync();
        if (Browser.CoreWebView2 is null)
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
            AppLogger.Info("http", $"WebView2 页面不在站点上，导航回首页恢复页内 fetch 上下文: {Browser.CoreWebView2.Source}");
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

        return uri.Host.Equals(_siteHost, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals($"www.{_siteHost}", StringComparison.OrdinalIgnoreCase);
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
