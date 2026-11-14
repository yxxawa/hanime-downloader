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

    /// <summary>Auto-complete after request recovery, or let the user finish after the browser page and Cookie are ready.</summary>
    public async Task<bool> VerifyAsync(bool forceRefresh = false, CancellationToken cancellationToken = default,
        bool showImmediately = false, Func<CancellationToken, Task<bool>>? confirmRecoveryAsync = null, Func<bool>? isRecoveryConfirmed = null,
        string? requestUrl = null, CloudflareChallengeObservation? observation = null,
        Func<CancellationToken, Task>? prepareBrowserAsync = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _verificationCancellation?.Cancel();
        _verificationCompletionSource?.TrySetResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _verificationCompletionSource = completion;
        using var verificationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _verificationCancellation = verificationCancellation;
        var token = verificationCancellation.Token;
        using var timing = AppLogger.IsCloudflareTimingEnabled ? new VerificationTiming(this, observation) : null;
        _confirmRecoveryAsync = confirmRecoveryAsync;
        _isRecoveryConfirmed = isRecoveryConfirmed ?? (() => true);
        _nextRecoveryCheckAt = DateTime.MinValue;
        _autoCompleteWhenReady = false;
        FinishButton.IsEnabled = false;
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
            if (showImmediately)
            {
                ShowForManualVerification("正在打开 Cloudflare 验证页面…");
                timing?.WindowShown();
            }
            // 显示窗口优先；后台交接期间不能确认旧文档，也不启动新的导航。
            if (prepareBrowserAsync is not null) await prepareBrowserAsync(token).WaitAsync(token);
            await EnsureInitializedAsync().WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (completion.Task.IsCompleted) return await completion.Task;
            if (Browser.CoreWebView2 is null) { completion.TrySetResult(false); return false; }
            if (forceRefresh) await ClearHanimeCookiesAsync(token);
            token.ThrowIfCancellationRequested();
            FinishButton.IsEnabled = true;
            StatusText.Text = showImmediately
                ? "请完成页面验证；通过后可点击“验证完成”，也可等待自动继续。"
                : "正在后台恢复会话并检查请求...";
            var target = VerificationRequestPolicy.ResolveTarget(_sessionContext, requestUrl) ?? SessionBaseUri;
            _confirmRecoveryAsync = confirmRecoveryAsync ?? (ct => ProbeVerificationRequestAsync(target.AbsoluteUri, ct));
            _navigationPending = true;
            _readyDocumentNavigationId = null;
            _autoCompleteWhenReady = true;
            timing?.TrackNavigation(Browser.CoreWebView2, target);
            Browser.CoreWebView2.Navigate(target.AbsoluteUri);
            _pollTimer.Start();
            if (!showImmediately)
            {
                var deadline = DateTime.UtcNow + HiddenChallengeGrace;
                while (!IsVisible && !completion.Task.IsCompleted && DateTime.UtcNow < deadline)
                {
                    await Task.WhenAny(completion.Task, Task.Delay(ClearancePollInterval, token));
                    if (!completion.Task.IsCompleted) token.ThrowIfCancellationRequested();
                }
                if (!IsVisible && !completion.Task.IsCompleted)
                {
                    ShowVerificationWindow("请在此窗口完成验证，通过后可点击“验证完成”继续。");
                    timing?.WindowShown();
                    Browser.CoreWebView2?.Reload();
                }
            }
            return await completion.Task;
        }
        catch
        {
            if (ReferenceEquals(_verificationCompletionSource, completion) && completion.TrySetResult(false)) Hide();
            throw;
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

    private async Task WaitForPageContentAsync(CoreWebView2 core, ulong navigationId, Uri target, string relativeUrl,
        CancellationToken cancellationToken)
    {
        var isWatchPage = relativeUrl.StartsWith("watch?", StringComparison.OrdinalIgnoreCase);
        var isSearchPage = relativeUrl.StartsWith("search?", StringComparison.OrdinalIgnoreCase);
        var maximumWait = isWatchPage || isSearchPage ? SearchWatchMaxWait : OtherPagesMaxWait;
        var startedAt = Stopwatch.GetTimestamp();

        while (true)
        {
            EnsureFetchDocumentIsCurrent(core, navigationId, cancellationToken);
            var payload = await core.ExecuteScriptAsync(
                "JSON.stringify({ ready: document.readyState, status: performance.getEntriesByType('navigation')[0]?.responseStatus ?? 0, bodyLength: document.body?.childElementCount ?? 0, challenge: typeof window._cf_chl_opt !== 'undefined' || document.title.toLowerCase().startsWith('just a moment'), resultLinks: document.querySelectorAll('.content-padding-new a[href], .home-rows-videos-wrapper a[href]').length })")
                .WaitAsync(cancellationToken);
            EnsureFetchDocumentIsCurrent(core, navigationId, cancellationToken);
            var state = DeserializeScriptResult<PageReadiness>(payload) ?? new PageReadiness();
            var elapsed = Stopwatch.GetElapsedTime(startedAt);

            if (state.Challenge)
                throw CloudflareDetection.ConfirmedChallenge(target.AbsoluteUri, new("navigation-dom", Stopwatch.GetTimestamp()));

            // DOM 已就绪时不等图片；搜索无结果仍保留短暂内容宽限期。
            if (state.Status >= 400 ||
                (state.Ready is "interactive" or "complete" && state.BodyLength > 0 &&
                 (!isSearchPage || state.ResultLinks > 0 || elapsed >= ContentRenderGrace)))
                return;

            if (elapsed >= maximumWait)
            {
                AppLogger.Info("cloudflare", $"等待页面内容超时: {relativeUrl}，elapsed={elapsed.TotalSeconds:0.0}s");
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

    private async void OnDomContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
    {
        if (e.NavigationId != _navigationId) return;
        _readyDocumentNavigationId = e.NavigationId;
        try
        {
            IsCurrentPageOnSite();
            await CheckVerificationStateAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Info("cloudflare", $"页面就绪后的验证检查异常: {ex.Message}");
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.NavigationId == _navigationId)
        {
            _navigationPending = false;
            _navigationStatusCode = e.HttpStatusCode;
        }
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
        if (Browser.CoreWebView2 is null || _isCheckingState || _isFinishingManually || !_autoCompleteWhenReady ||
            completion is null || completion.Task.IsCompleted || cancellation is null || cancellation.IsCancellationRequested || confirm is null) return;
        var token = cancellation.Token;
        _isCheckingState = true;
        try
        {
            if (!await TryRefreshReadySessionAsync(token))
            {
                if (!completion.Task.IsCompleted && !token.IsCancellationRequested)
                    StatusText.Text = _sessionReadinessMessage;
                return;
            }
            if (!ReferenceEquals(completion, _verificationCompletionSource) || completion.Task.IsCompleted || token.IsCancellationRequested) return;
            if (DateTime.UtcNow < _nextRecoveryCheckAt) return;
            _nextRecoveryCheckAt = DateTime.UtcNow + RecoveryCheckInterval;
            StatusText.Text = "页面验证已通过，正在自动检查；也可点击“验证完成”立即继续。";
            // 手动完成不受远端探测锁阻塞，完成时也会取消这个等待。
            var recovered = await confirm(token).WaitAsync(token);
            if (!ReferenceEquals(completion, _verificationCompletionSource) || completion.Task.IsCompleted || token.IsCancellationRequested) return;
            if (!recovered)
            {
                StatusText.Text = "已捕获有效的验证 Cookie，但原请求尚未恢复；可重试当前页面，或点击“验证完成”继续。";
                return;
            }
            // 检查后的 Cookie 可能变化；自动完成仍要求页面与原请求均已恢复。
            if (await TryRefreshReadySessionAsync(token) &&
                ReferenceEquals(completion, _verificationCompletionSource) && !completion.Task.IsCompleted &&
                !token.IsCancellationRequested && _isRecoveryConfirmed?.Invoke() == true) CompleteVerification();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(completion, _verificationCompletionSource) && !completion.Task.IsCompleted)
            {
                StatusText.Text = "自动检查暂未成功；页面验证完成后可点击“验证完成”。";
                AppLogger.Info("cloudflare", $"恢复检查失败: {ex.GetType().Name}");
            }
        }
        finally
        {
            _isCheckingState = false;
        }
    }

    private async void FinishButton_OnClick(object sender, RoutedEventArgs e)
    {
        await CompleteVerificationManuallyAsync();
    }

    private async Task CompleteVerificationManuallyAsync()
    {
        var completion = _verificationCompletionSource;
        var cancellation = _verificationCancellation;
        if (!_autoCompleteWhenReady || _isFinishingManually || completion is null || completion.Task.IsCompleted ||
            cancellation is null || cancellation.IsCancellationRequested) return;
        var token = cancellation.Token;
        _isFinishingManually = true;
        FinishButton.IsEnabled = false;
        try
        {
            // 用户确认后只读取当前页面和 Cookie，不再重复访问所有失败请求。
            if (!await TryRefreshReadySessionAsync(token))
            {
                StatusText.Text = _sessionReadinessMessage;
                return;
            }
            if (ReferenceEquals(completion, _verificationCompletionSource) && !completion.Task.IsCompleted &&
                !token.IsCancellationRequested) CompleteVerification();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(completion, _verificationCompletionSource) && !completion.Task.IsCompleted)
                StatusText.Text = "暂时无法读取验证结果，请稍后再次点击“验证完成”。";
            AppLogger.Info("cloudflare", $"手动完成验证失败: {ex.GetType().Name}");
        }
        finally
        {
            _isFinishingManually = false;
            if (ReferenceEquals(completion, _verificationCompletionSource) && !completion.Task.IsCompleted && !token.IsCancellationRequested)
                FinishButton.IsEnabled = true;
        }
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
        _verificationCancellation?.Cancel();
    }
}
