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

    /// <summary>Show verification when needed, then continue automatically only after the failed requests recover.</summary>
    public async Task<bool> VerifyAsync(bool forceRefresh = false, CancellationToken cancellationToken = default,
        bool showImmediately = false, Func<CancellationToken, Task<bool>>? confirmRecoveryAsync = null, Func<bool>? isRecoveryConfirmed = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _verificationCancellation?.Cancel();
        _verificationCompletionSource?.TrySetResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _verificationCompletionSource = completion;
        using var verificationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _verificationCancellation = verificationCancellation;
        var token = verificationCancellation.Token;
        _confirmRecoveryAsync = confirmRecoveryAsync ?? (ct => ProbeVerificationRequestAsync(null, ct));
        _isRecoveryConfirmed = isRecoveryConfirmed ?? (() => true);
        _nextRecoveryCheckAt = DateTime.MinValue;
        _autoCompleteWhenReady = true;
        using var registration = token.Register(() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_verificationCompletionSource, completion) || completion.Task.IsCompleted) return;
            _pollTimer.Stop();
            _autoCompleteWhenReady = false;
            completion.TrySetResult(false);
            Hide();
        })));
        try
        {
            if (showImmediately) ShowForManualVerification("请完成 Cloudflare 验证，通过后将检查原请求并自动继续。");
            await EnsureInitializedAsync();
            token.ThrowIfCancellationRequested();
            if (completion.Task.IsCompleted) return await completion.Task;
            if (Browser.CoreWebView2 is null) { completion.TrySetResult(false); return false; }
            if (forceRefresh) await ClearHanimeCookiesAsync();
            FinishButton.IsEnabled = false;
            StatusText.Text = showImmediately
                ? "请完成页面验证；原请求恢复后，本窗口会自动收起。"
                : "正在后台恢复会话并检查请求...";
            _navigationPending = true;
            Browser.CoreWebView2.Navigate(_siteBaseUrl);
            _pollTimer.Start();
            if (!showImmediately)
            {
                var deadline = DateTime.UtcNow + HiddenChallengeGrace;
                while (!IsVisible && !completion.Task.IsCompleted && DateTime.UtcNow < deadline)
                    await Task.Delay(ClearancePollInterval, token);
                if (!IsVisible && !completion.Task.IsCompleted)
                {
                    ShowVerificationWindow("请在此窗口完成验证，原请求恢复后将自动继续。");
                    Browser.CoreWebView2?.Reload();
                }
            }
            return await completion.Task;
        }
        finally
        {
            if (ReferenceEquals(_verificationCompletionSource, completion))
            {
                _pollTimer.Stop();
                _autoCompleteWhenReady = false;
                _confirmRecoveryAsync = null;
                _isRecoveryConfirmed = null;
                _verificationCancellation = null;
            }
        }
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
                    throw new CloudflareChallengeException("搜索遇到 Cloudflare 验证，当前会话失效，请重新验证。", new Uri(SessionBaseUri, relativeUrl).AbsoluteUri);
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
        if (e.NavigationId == _navigationId) _navigationPending = false;
        if (!e.IsSuccess || Browser.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            // Also track redirects while hidden; the configured profile stays unchanged.
            IsCurrentPageOnSite();
            await CheckVerificationStateAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Info("cloudflare", $"验证状态检查异常: {ex.Message}");
        }
    }

    private async Task CheckVerificationStateAsync()
    {
        var completion = _verificationCompletionSource;
        var cancellation = _verificationCancellation;
        var confirm = _confirmRecoveryAsync;
        if (Browser.CoreWebView2 is null || _isCheckingState || !_autoCompleteWhenReady || completion is null || completion.Task.IsCompleted ||
            cancellation is null || cancellation.IsCancellationRequested || confirm is null) return;
        var token = cancellation.Token;
        _isCheckingState = true;
        try
        {
            FinishButton.IsEnabled = false;
            var payload = await Browser.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({ html: document.documentElement?.outerHTML ?? '', title: document.title, ready: document.readyState, href: location.href })");
            var state = DeserializeScriptResult<PageState>(payload) ?? new PageState();
            if (CloudflareDetection.IsChallengePage(state.Html ?? string.Empty, state.Title))
            {
                StatusText.Text = "Cloudflare 验证尚未通过，请完成页面中的验证。";
                return;
            }
            if (_navigationPending || state.Ready is not ("interactive" or "complete") || !IsCurrentPageOnSite() ||
                !Uri.TryCreate(state.Href, UriKind.Absolute, out var pageUri) ||
                pageUri.GetLeftPart(UriPartial.Authority) != SessionBaseUri.GetLeftPart(UriPartial.Authority)) return;
            Cookies = await _cookieBridge.ExportCookiesAsync(Browser.CoreWebView2.CookieManager);
            CookieHeader = _cookieBridge.BuildCookieHeader(Cookies);
            if (!HasValidClearance())
            {
                StatusText.Text = "正在等待当前站点的有效验证 Cookie...";
                return;
            }
            FinishButton.IsEnabled = true;
            if (DateTime.UtcNow < _nextRecoveryCheckAt) return;
            _nextRecoveryCheckAt = DateTime.UtcNow + RecoveryCheckInterval;
            FinishButton.IsEnabled = false;
            StatusText.Text = "验证页面已通过，正在确认原请求恢复...";
            var recovered = await confirm(token);
            if (!ReferenceEquals(completion, _verificationCompletionSource) || completion.Task.IsCompleted || token.IsCancellationRequested) return;
            if (!recovered)
            {
                StatusText.Text = "原请求仍未恢复，暂不关闭窗口；稍后自动检查，也可点击“重新检查”。";
                return;
            }
            // Probing may rotate cookies. Persist the post-probe session, not an earlier snapshot.
            Cookies = await _cookieBridge.ExportCookiesAsync(Browser.CoreWebView2.CookieManager);
            CookieHeader = _cookieBridge.BuildCookieHeader(Cookies);
            if (ReferenceEquals(completion, _verificationCompletionSource) && !completion.Task.IsCompleted &&
                !token.IsCancellationRequested && !_navigationPending && HasValidClearance() && _isRecoveryConfirmed?.Invoke() == true) CompleteVerification();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(completion, _verificationCompletionSource) && !completion.Task.IsCompleted)
            {
                StatusText.Text = "原请求检查暂未成功，将自动重试；验证窗口保持打开。";
                AppLogger.Info("cloudflare", $"恢复检查失败: {ex.GetType().Name}");
            }
        }
        finally
        {
            _isCheckingState = false;
            if (ReferenceEquals(completion, _verificationCompletionSource) && !completion.Task.IsCompleted && !token.IsCancellationRequested)
                FinishButton.IsEnabled = true;
        }
    }

    private async void FinishButton_OnClick(object sender, RoutedEventArgs e)
    {
        // A manual check follows exactly the same gate; it cannot force an unverified success.
        _nextRecoveryCheckAt = DateTime.MinValue;
        await CheckVerificationStateAsync();
    }

    /// <summary>显示同一个验证窗口，但不关闭验证后的自动检查与继续。</summary>
    public void ShowForManualVerification(string status)
    {
        if (_verificationCompletionSource is null || _verificationCompletionSource.Task.IsCompleted)
        {
            return;
        }

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
