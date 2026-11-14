using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Hanime1Downloader.CSharp.Views;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppThemeService = Hanime1Downloader.CSharp.Services.AppTheme;

namespace Hanime1Downloader.CSharp;

public partial class MainWindow
{

    private readonly CancellationTokenSource _startupSessionCancellation = new();
    private Task<string>? _startupSessionTask;
    private CancellationTokenSource? _cachedCookieImportCancellation;
    private Task? _cachedCookieImportTask;
    private SessionVerification? _sessionVerification;

    private CloudflareWindow GetSessionWindow()
    {
        if (_cloudflareWindow is not null) return _cloudflareWindow;
        var window = new CloudflareWindow(_settings.SiteHost) { Owner = this };
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_cloudflareWindow, window)) return;
            _cloudflareWindow = null;
            _accountService = null;
            _accountServiceSiteHost = string.Empty;
        };
        return _cloudflareWindow = window;
    }

    private async Task<string> RestoreStartupSessionAsync(CancellationToken cancellationToken)
    {
        var startupStatus = "已启动，如遇访问问题请手动验证。";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var browser = GetSessionWindow();

            // 先静默复用浏览器里现存的 Cloudflare 会话（后台通过托管挑战，不弹窗）。
            // 成功就完全不碰磁盘缓存，避免用旧 Cookie 覆盖掉仍然有效的会话。
            var reused = await TryReuseSessionSilentlyAsync(browser, "启动时自动恢复会话", cancellationToken);

            // 会话确实不可用时，才导入磁盘缓存里的 Cookie 再试一次。
            cancellationToken.ThrowIfCancellationRequested();
            if (!reused)
            {
                var cached = LoadCookieCache();
                if (cached.Count > 0)
                {
                    try
                    {
                        await browser.ImportCookiesAsync(cached, cancellationToken);
                        reused = await TryReuseSessionSilentlyAsync(browser, "导入缓存 Cookie 后自动恢复会话", cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        LogInfo("cloudflare", $"导入缓存 Cookie 后自动恢复失败: {ex.Message}");
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (reused)
            {
                startupStatus = $"已自动恢复 Cloudflare 会话。当前共 {_appState.Cookies.Count} 个 Cookie。";
            }
            else
            {
                InitSessionWithoutCf();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 搜索或手动验证接管会话，不再导入旧 Cookie 或重置客户端。
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // 站点切换或关闭可同时释放旧 WebView2；不要以旧错误重置新会话。
        }
        catch (Exception ex)
        {
            HandleUiActionError("startup", "初始化失败", ex);
            InitSessionWithoutCf();
        }

        return startupStatus;
    }

    private void CancelBackgroundSessionWork()
    {
        _startupSessionCancellation.Cancel();
        _cachedCookieImportCancellation?.Cancel();
    }

    private async Task StopBackgroundSessionWorkAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelBackgroundSessionWork();
        var startup = _startupSessionTask;
        var importing = _cachedCookieImportTask;
        if (startup is not null) await startup.WaitAsync(cancellationToken);
        if (importing is not null) await importing.WaitAsync(cancellationToken);
    }

    private void CancelSessionOperations()
    {
        CancelBackgroundSessionWork();
        var verification = _sessionVerification;
        _sessionVerification = null;
        verification?.Cancellation.Cancel();
    }

    private void InitSessionWithoutCf(IReadOnlyList<BrowserCookieRecord>? cookies = null, string? browserVersion = null)
    {
        GetSessionWindow();
        var effectiveCookies = cookies ?? [];
        ReplaceHttpSession(effectiveCookies, browserVersion);
    }

    private async void VerifyButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunVerificationAsync(forceRefresh: false);
        }
        catch (Exception ex)
        {
            HandleUiActionError("cloudflare", "验证失败", ex);
        }
    }

    private async Task RunVerificationAsync(bool forceRefresh)
    {
        var verified = await WaitForSharedVerificationAsync("正在打开 Cloudflare 验证窗口。", CancellationToken.None,
            requestUrl: null, observation: null, activateExisting: true, forceRefresh: forceRefresh);
        if (!verified && !_isWindowClosing)
            StatusText.Text = "还没有拿到可用 Cookie，请在验证窗口中完成站点验证后再继续。";
    }

    private async Task<bool> SyncVerifiedSessionAsync(CloudflareWindow browser, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isWindowClosing || !ReferenceEquals(_cloudflareWindow, browser)) return false;

        var cookies = browser.Cookies.ToList();
        var header = browser.CookieHeader;
        var browserVersion = browser.BrowserVersion;
        var cachePath = GetCookieCacheFilePath();
        await AtomicFile.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cookies, FavoritesJsonOptions), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_isWindowClosing || !ReferenceEquals(_cloudflareWindow, browser)) return false;

        _appState.Cookies = cookies;
        _appState.CookieHeader = header;
        _appState.BrowserVersion = browserVersion;
        ReplaceHttpSession(cookies, browserVersion);
        _videoDetailsCache.Clear();
        _videoDetailsInFlight.Clear();
        return true;
    }

    private async Task<bool> TryReuseSessionSilentlyAsync(CloudflareWindow browser, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var reused = await browser.TryReuseSessionAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!reused || !await SyncVerifiedSessionAsync(browser, cancellationToken))
            {
                LogInfo("cloudflare", $"{reason}：浏览器会话暂不可用");
                return false;
            }

            LogInfo("cloudflare", $"{reason}：已自动恢复 Cloudflare 会话，无需手动验证");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogInfo("cloudflare", $"{reason}：自动恢复异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 替换 HttpClient 会话。下载进行中时旧客户端进入退役列表（在途下载用完后由队列循环释放），
    /// 避免 Dispose 正在使用的 HttpClient 导致在途下载失败。
    /// </summary>
    private void ReplaceHttpSession(IReadOnlyList<BrowserCookieRecord> cookies, string? browserVersion)
    {
        var oldClient = _httpClient;
        var sessionHost = _cloudflareWindow?.SessionBaseUri.Host ?? _settings.SiteHost;
        _httpClient = new HanimeHttpClientFactory().Create(cookies, sessionHost, browserVersion);
        _apiClient = new HanimeApiClient(_cloudflareWindow!, _httpClient, sessionHost);
        _downloadService = new DownloadService(_httpClient, sessionHost, CreateDownloadRetryPolicy());

        if (oldClient is null)
        {
            return;
        }

        if (_isDownloadingQueue)
        {
            lock (_retiredHttpClients)
            {
                _retiredHttpClients.Add(oldClient);
            }
        }
        else
        {
            oldClient.Dispose();
        }
    }

    /// <summary>释放全部退役客户端（队列运行结束时调用）。</summary>
    private void DisposeRetiredHttpClients()
    {
        lock (_retiredHttpClients)
        {
            foreach (var client in _retiredHttpClients)
            {
                client.Dispose();
            }
            _retiredHttpClients.Clear();
        }
    }

    private Task SyncCachedCookiesToBrowserAsync(IReadOnlyList<BrowserCookieRecord> cookies)
    {
        if (cookies.Count == 0 || _isWindowClosing) return Task.CompletedTask;
        _cachedCookieImportCancellation?.Cancel();
        var browser = GetSessionWindow();
        var cancellation = new CancellationTokenSource();
        _cachedCookieImportCancellation = cancellation;
        return _cachedCookieImportTask = ImportCachedCookiesAsync(browser, cookies, cancellation);
    }

    private async Task ImportCachedCookiesAsync(CloudflareWindow browser, IReadOnlyList<BrowserCookieRecord> cookies,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await browser.ImportCookiesAsync(cookies, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_cloudflareWindow, browser)) return;

            _appState.Cookies = browser.Cookies.ToList();
            _appState.CookieHeader = browser.CookieHeader;
            _appState.BrowserVersion = browser.BrowserVersion;
            ReplaceHttpSession(_appState.Cookies, _appState.BrowserVersion);
            LogInfo("cloudflare", $"已同步 {_settings.SiteHost} 的缓存 Cookie 到浏览器会话，共 {_appState.Cookies.Count} 个");
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogError("cloudflare", $"同步 {_settings.SiteHost} 的缓存 Cookie 到浏览器会话失败", ex);
        }
        finally
        {
            if (ReferenceEquals(_cachedCookieImportCancellation, cancellation)) _cachedCookieImportCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task RefreshQueueUrlsAfterSessionRestoreAsync()
    {
        try { await RefreshQueueUrlsAfterSessionRestoreCoreAsync(); }
        catch (Exception ex) { LogError("queue", "会话恢复后后台刷新队列失败", ex); }
    }

    private async Task RefreshQueueUrlsAfterSessionRestoreCoreAsync()
    {
        if (_apiClient is null)
        {
            return;
        }

        var itemsToRefresh = _downloadQueue.Where(item => !string.IsNullOrWhiteSpace(item.VideoId)).ToList();
        if (itemsToRefresh.Count == 0)
        {
            return;
        }

        var distinctItems = itemsToRefresh
            .Where(item => !item.IsDownloading)
            .GroupBy(item => item.VideoId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        LogInfo("queue", $"正在为 {distinctItems.Count} 个队列项重新获取下载链接...");
        var refreshed = 0;
        var refreshGate = new SemaphoreSlim(2);
        var tasks = distinctItems.Select(async item =>
        {
            await refreshGate.WaitAsync();
            try
            {
                var match = await ResolveQueueItemSourceAsync(item, backgroundRefresh: true);
                if (match is not null)
                {
                    Interlocked.Increment(ref refreshed);
                }
            }
            catch (Exception ex)
            {
                LogError("queue", $"会话恢复后刷新队列链接失败: videoId={item.VideoId}", ex);
            }
            finally
            {
                refreshGate.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks);

        await TrySaveDownloadQueueAsync("queue", "保存下载队列失败");
        LogInfo("queue", $"已为 {refreshed}/{distinctItems.Count} 个队列项刷新下载链接。");
    }

    private static bool IsCloudflareSessionError(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
        {
            return true;
        }

        return ex.Message.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("cf_clearance", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("status=403", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("403 (Forbidden)", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断 URL 是否属于当前站点（而非媒体 CDN 等外部域名）。</summary>
    private bool IsSiteHostUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, _settings.SiteHost, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, $"www.{_settings.SiteHost}", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SessionVerification
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly VerificationRequestSet Requests = new();
        public readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WaiterCount;
    }

    private Task<bool> EnsureVerifiedSessionAsync(string reason, CancellationToken cancellationToken = default,
        Exception? failure = null, string? fallbackRequestUrl = null)
    {
        var challenge = failure as CloudflareChallengeException;
        return WaitForSharedVerificationAsync(reason, cancellationToken, challenge?.RequestUrl ?? fallbackRequestUrl, challenge?.Observation);
    }

    private async Task<bool> WaitForSharedVerificationAsync(string reason, CancellationToken cancellationToken, string? requestUrl,
        CloudflareChallengeObservation? observation, bool activateExisting = false, bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isWindowClosing) return false;

        var startNew = _sessionVerification is null;
        var verification = _sessionVerification ??= new SessionVerification();
        verification.Requests.Add(requestUrl);
        verification.WaiterCount++;
        try
        {
            if (startNew)
                _ = RunSharedVerificationAsync(verification, reason, requestUrl, observation, forceRefresh);
            else if (activateExisting && _cloudflareWindow is { IsVisible: true } browser)
                browser.Activate(); // Only an explicit manual click may bring the existing window to the foreground.

            return await verification.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            verification.Requests.Remove(requestUrl);
            verification.WaiterCount--;
            if (verification.WaiterCount == 0 && !verification.Completion.Task.IsCompleted &&
                ReferenceEquals(_sessionVerification, verification))
            {
                _sessionVerification = null;
                verification.Cancellation.Cancel();
            }
        }
    }

    private async Task RunSharedVerificationAsync(SessionVerification verification, string reason, string? requestUrl,
        CloudflareChallengeObservation? observation, bool forceRefresh)
    {
        var token = verification.Cancellation.Token;
        try
        {
            var operationId = CreateOperationId("cfrecover");
            var browser = GetSessionWindow();
            StatusText.Text = $"{reason} 请在弹出的窗口中完成验证。";
            LogInfo("cloudflare", $"[{operationId}] {reason}");
            var verified = await browser.VerifyAsync(forceRefresh, token, showImmediately: true,
                confirmRecoveryAsync: ct => verification.Requests.ConfirmAsync(browser.ProbeVerificationRequestAsync, ct),
                isRecoveryConfirmed: () => verification.Requests.IsConfirmed, requestUrl: requestUrl, observation: observation,
                prepareBrowserAsync: StopBackgroundSessionWorkAsync);
            token.ThrowIfCancellationRequested();

            if (!verified || browser.Cookies.Count == 0 || string.IsNullOrWhiteSpace(browser.CookieHeader) ||
                !await SyncVerifiedSessionAsync(browser, token))
            {
                StatusText.Text = "Cloudflare 会话恢复未完成，请完成站点验证后再试。";
                LogInfo("cloudflare", $"[{operationId}] Cloudflare 会话恢复未完成");
                verification.Completion.TrySetResult(false);
                return;
            }

            StatusText.Text = $"Cloudflare 会话已恢复，当前共 {_appState.Cookies.Count} 个 Cookie。";
            LogInfo("cloudflare", $"[{operationId}] Cloudflare 会话已恢复，Cookie 数量 {_appState.Cookies.Count}");
            verification.Completion.TrySetResult(true);
            var recoveredClient = _apiClient;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_isWindowClosing && ReferenceEquals(recoveredClient, _apiClient))
                    _ = RefreshQueueUrlsAfterSessionRestoreAsync();
            }));
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            verification.Completion.TrySetResult(false);
        }
        catch (Exception ex)
        {
            verification.Completion.TrySetException(ex);
            _ = verification.Completion.Task.Exception;
        }
        finally
        {
            if (ReferenceEquals(_sessionVerification, verification)) _sessionVerification = null;
            verification.Cancellation.Dispose();
        }
    }
}
