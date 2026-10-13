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

    /// <summary>
    /// 获取/恢复 Cloudflare 会话。自动恢复先静默验证，手动验证立即显示窗口并由用户确认完成。
    /// </summary>
    public async Task<bool> VerifyAsync(bool forceRefresh = false, CancellationToken cancellationToken = default, bool showImmediately = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _verificationCompletionSource?.TrySetResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _verificationCompletionSource = completion;
        _autoCompleteWhenReady = !showImmediately;
        if (showImmediately)
        {
            ShowForManualVerification("正在初始化内置浏览器，请在此窗口完成 Cloudflare 验证。");
        }

        await EnsureInitializedAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (completion.Task.IsCompleted)
        {
            return await completion.Task;
        }

        // 用户取消（暂停等场景）时停止等待验证，不再无限挂起。
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            _pollTimer.Stop();
            completion.TrySetResult(false);
        });

        if (Browser.CoreWebView2 is null)
        {
            completion.TrySetResult(false);
            return await completion.Task;
        }

        if (forceRefresh)
        {
            await ClearHanimeCookiesAsync();
        }
        else
        {
            FinishButton.IsEnabled = false;
            StatusText.Text = !_autoCompleteWhenReady
                ? "请在此窗口完成 Cloudflare 验证，然后点击“验证完成并继续”。"
                : "正在后台获取 Cloudflare 会话，无需手动操作...";
        }

        Browser.CoreWebView2.Navigate(_siteBaseUrl);
        _pollTimer.Start();

        if (!_autoCompleteWhenReady)
        {
            return await completion.Task;
        }

        // 后台静默等待：窗口保持隐藏，托管挑战由挑战脚本自动完成。
        var hiddenDeadline = DateTime.UtcNow + HiddenChallengeGrace;
        while (_autoCompleteWhenReady && !completion.Task.IsCompleted && DateTime.UtcNow < hiddenDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(ClearancePollInterval, cancellationToken);
        }

        if (_autoCompleteWhenReady && !completion.Task.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            AppLogger.Info("cloudflare", $"后台自动验证超过 {HiddenChallengeGrace.TotalSeconds:0} 秒仍未完成，显示验证窗口兜底");
            await Dispatcher.InvokeAsync(() =>
            {
                ShowVerificationWindow("自动验证未完成，请在此窗口完成 Cloudflare 验证。");
                if (Browser.CoreWebView2 is not null)
                {
                    Browser.CoreWebView2.Reload();
                }
            });
        }

        return await completion.Task;
    }

    private async Task WaitForPageContentAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        var isWatchPage = relativeUrl.StartsWith("watch?", StringComparison.OrdinalIgnoreCase);
        var isSearchPage = relativeUrl.StartsWith("search?", StringComparison.OrdinalIgnoreCase);
        var maximumWait = isWatchPage || isSearchPage ? SearchWatchMaxWait : OtherPagesMaxWait;
        await WaitForPageReadyAsync(relativeUrl, isWatchPage, isSearchPage, maximumWait, cancellationToken);
    }

    private async Task WaitForPageReadyAsync(string relativeUrl, bool isWatchPage, bool isSearchPage, TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        var minimumWait = TimeSpan.FromMilliseconds(isWatchPage || isSearchPage ? 35 : 15);
        var startedAt = DateTime.UtcNow;
        var challengeLogged = false;
        var shownForChallenge = false;
        var lastVisibility = string.Empty;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await Browser.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({ ready: document.readyState, bodyLength: document.body ? document.body.childElementCount : 0, challenge: typeof window._cf_chl_opt !== 'undefined' || (document.title || '').indexOf('Just a moment') === 0, visibility: document.visibilityState, focused: document.hasFocus(), resultLinks: document.querySelectorAll('.content-padding-new a[href], .home-rows-videos-wrapper a[href]').length })");
            var state = DeserializeScriptResult<PageReadiness>(payload) ?? new PageReadiness();
            var elapsed = DateTime.UtcNow - startedAt;

            if (state.Challenge)
            {
                // 搜索已确认遇到挑战：立即交给会话恢复流程显示窗口，不等待后台挑战宽限期。
                if (isSearchPage)
                {
                    throw new InvalidOperationException("搜索遇到 Cloudflare 验证，当前会话失效，请重新验证。");
                }

                if (!string.Equals(state.Visibility, lastVisibility, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Info("cloudflare", $"挑战等待中 visibility={state.Visibility}, focused={state.Focused}, IsVisible={IsVisible}, shown={shownForChallenge}");
                    lastVisibility = state.Visibility ?? string.Empty;
                }

                // 托管挑战会自动通过并跳转到真实页面，继续等待。
                // 实测（WebView2 152 + InitializeBrowserAsync 里的反节流启动参数）：
                // 窗口保持隐藏时挑战同样会自动通过，典型耗时 1-4 秒；
                // 因此这里不再为了通过挑战而弹出窗口，只有长时间未通过才显示窗口兜底。
                if (!shownForChallenge && elapsed >= HiddenChallengeGrace)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsActiveVerification() && !IsVisible)
                        {
                            AppLogger.Info("cloudflare", $"挑战在后台等待 {elapsed.TotalSeconds:0.0}s 仍未通过，显示验证窗口兜底");
                            ShowVerificationWindow("自动验证未完成，请在此窗口完成 Cloudflare 验证。");
                            shownForChallenge = true;
                            if (Browser.CoreWebView2 is not null)
                            {
                                Browser.CoreWebView2.Reload();
                            }
                        }
                    });
                }

                if (!challengeLogged)
                {
                    AppLogger.Info("cloudflare", $"等待托管挑战自动通过: {relativeUrl}（最长 {maximumWait.TotalSeconds:0} 秒）");
                    challengeLogged = true;
                }

                if (elapsed >= maximumWait)
                {
                    AppLogger.Info("cloudflare", $"等待挑战超时: {relativeUrl}，elapsed={elapsed.TotalSeconds:0.0}s，返回挑战页快照交由下游处理");
                    return;
                }

                await Task.Delay(PagePollInterval, cancellationToken);
                continue;
            }

            if (shownForChallenge)
            {
                // 挑战已通过（或页面已跳离挑战页）：恢复隐藏状态。
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsActiveVerification())
                    {
                        Hide();
                    }
                });
                shownForChallenge = false;
            }

            var pageReady = string.Equals(state.Ready, "complete", StringComparison.OrdinalIgnoreCase) && state.BodyLength > 0;
            if (pageReady && elapsed >= minimumWait)
            {
                // 相关视频与结果卡片都是服务端渲染，readyState complete 时已在 DOM 中，
                // 无需等待 related/recommend 链接出现（此前会造成无谓的延迟）。
                var resultReady = state.ResultLinks > 0;
                var contentReady = !isSearchPage || resultReady;

                if (contentReady || elapsed >= ContentRenderGrace || (!isWatchPage && !isSearchPage))
                {
                    return;
                }
            }

            if (elapsed >= maximumWait)
            {
                // 防御性兜底：页面始终未就绪时返回当前状态，沿用原有错误处理流程。
                AppLogger.Info("cloudflare", $"等待页面内容超时: {relativeUrl}，elapsed={elapsed.TotalSeconds:0.0}s，challenge={state.Challenge}");
                return;
            }

            await Task.Delay(PagePollInterval, cancellationToken);
        }
    }

    /// <summary>用户正在通过验证窗口手动验证（窗口可见且等待用户完成）。</summary>
    private bool IsActiveVerification()
    {
        return IsVisible && _verificationCompletionSource is not null && !_verificationCompletionSource.Task.IsCompleted;
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || Browser.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await CheckVerificationStateAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Info("cloudflare", $"验证状态检查异常: {ex.Message}");
        }
    }

    private async Task CheckVerificationStateAsync()
    {
        if (Browser.CoreWebView2 is null || _isCheckingState) return;
        if (!IsVisible && !_autoCompleteWhenReady) return;
        _isCheckingState = true;
        try
        {
        var payload = await Browser.CoreWebView2.ExecuteScriptAsync(
            "JSON.stringify({ html: document.documentElement?.outerHTML ?? '', title: document.title, ready: document.readyState, href: location.href, bodyText: document.body?.innerText ?? '' })");
        var state = DeserializeScriptResult<PageState>(payload) ?? new PageState();
        var html = state.Html ?? string.Empty;

        var challengePresent = CloudflareDetection.IsChallengePage(html, state.Title);
        if (challengePresent)
        {
            StatusText.Text = $"{_siteHost} 正在进行 Cloudflare 验证，请保持此窗口打开并等待页面自动跳转。";
            FinishButton.IsEnabled = true;
            return;
        }

        Cookies = await _cookieBridge.ExportCookiesAsync(Browser.CoreWebView2.CookieManager);
        CookieHeader = _cookieBridge.BuildCookieHeader(Cookies);
        var hasClearance = Cookies.Any(cookie => cookie.Name.Equals("cf_clearance", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(cookie.Value));

        FinishButton.IsEnabled = true;
        StatusText.Text = hasClearance ? ((_autoCompleteWhenReady ? $"{_siteHost} 已拿到 cf_clearance，正在自动继续。" : $"{_siteHost} 已拿到 cf_clearance，可以继续使用。")) : $"已进入 {_siteHost} 站点主页，可点击按钮手动获取 Cookie。";
        if (_autoCompleteWhenReady && hasClearance)
        {
            CompleteVerification();
        }
        }
        finally { _isCheckingState = false; }
    }

    private async void FinishButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null) return;
        Cookies = await _cookieBridge.ExportCookiesAsync(Browser.CoreWebView2.CookieManager);
        CookieHeader = _cookieBridge.BuildCookieHeader(Cookies);
        _autoCompleteWhenReady = false;
        CompleteVerification();
    }

    /// <summary>将正在进行的恢复切换为手动验证，立即显示同一个窗口。</summary>
    public void ShowForManualVerification(string status)
    {
        if (_verificationCompletionSource is null || _verificationCompletionSource.Task.IsCompleted)
        {
            return;
        }

        _autoCompleteWhenReady = false;
        ShowVerificationWindow(status);
    }

    /// <summary>手动验证和兜底共用：恢复窗口状态并显式定位，避免沿用后台初始化的屏幕外坐标。</summary>
    private void ShowVerificationWindow(string status)
    {
        try
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowState = WindowState.Normal;
            ShowInTaskbar = true;
            WindowStyle = WindowStyle.SingleBorderWindow;
            Topmost = false;
            Width = 520;
            Height = 480;
            // WindowStartupLocation 只影响首次 Show；复用隐藏窗口必须直接重设坐标。
            var bounds = Owner is { IsVisible: true, WindowState: not WindowState.Minimized }
                ? Owner.RestoreBounds
                : SystemParameters.WorkArea;
            if (bounds.IsEmpty)
            {
                bounds = SystemParameters.WorkArea;
            }

            Left = Math.Clamp(bounds.Left + (bounds.Width - Width) / 2,
                SystemParameters.VirtualScreenLeft,
                Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
            Top = Math.Clamp(bounds.Top + (bounds.Height - Height) / 2,
                SystemParameters.VirtualScreenTop,
                Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
            StatusText.Text = status;
            if (!IsVisible)
            {
                Show();
            }

            Activate();
        }
        catch (Exception ex)
        {
            AppLogger.Error("cloudflare", "显示验证窗口失败", ex);
            throw;
        }
    }

    private void CompleteVerification()
    {
        _pollTimer.Stop();
        FinishButton.IsEnabled = false;
        StatusText.Text = "验证完成，已保留浏览器会话。";
        Hide();
        _autoCompleteWhenReady = false;
        _verificationCompletionSource?.TrySetResult(true);
    }
}
