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

    private void InitSessionWithoutCf(IReadOnlyList<BrowserCookieRecord>? cookies = null, string? browserVersion = null)
    {
        _cloudflareWindow ??= new CloudflareWindow(_settings.SiteHost) { Owner = this };
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
        var operationId = CreateOperationId("cfverify");
        _cloudflareWindow ??= new CloudflareWindow(_settings.SiteHost) { Owner = this };
        LogInfo("cloudflare", $"[{operationId}] 开始验证，会话站点={_settings.SiteHost}, forceRefresh={forceRefresh}");
        var verified = await _cloudflareWindow.VerifyAsync(forceRefresh);
        if (!verified || _cloudflareWindow.Cookies.Count == 0 || string.IsNullOrWhiteSpace(_cloudflareWindow.CookieHeader))
        {
            LogInfo("cloudflare", $"[{operationId}] 验证未拿到可用 Cookie");
            StatusText.Text = "还没有拿到可用 Cookie，请在验证窗口中完成站点验证后再继续。";
            return;
        }

        await SyncVerifiedSessionAsync();
        StatusText.Text = forceRefresh
            ? $"已强制重验并同步 Cookie。当前共 {_appState.Cookies.Count} 个 Cookie。"
            : $"已同步当前 Cloudflare 会话。当前共 {_appState.Cookies.Count} 个 Cookie。";
        LogInfo("cloudflare", $"[{operationId}] 验证完成，Cookie={_appState.Cookies.Count}");
    }

    private async Task SyncVerifiedSessionAsync()
    {
        if (_cloudflareWindow is null)
        {
            return;
        }

        _appState.Cookies = _cloudflareWindow.Cookies.ToList();
        _appState.CookieHeader = _cloudflareWindow.CookieHeader;
        _appState.BrowserVersion = _cloudflareWindow.BrowserVersion;
        await SaveCookieCacheAsync();
        ReplaceHttpSession(_appState.Cookies, _appState.BrowserVersion);
        _videoDetailsCache.Clear();
        _videoDetailsInFlight.Clear();
    }

    /// <summary>
    /// 静默复用/恢复 Cloudflare 会话：成功则同步 Cookie 到 HttpClient，失败只记日志。
    /// 全程不需要用户操作，验证窗口只在后台长时间无法通过时才由 CloudflareWindow 兜底弹出。
    /// </summary>
    private async Task<bool> TryReuseSessionSilentlyAsync(string reason)
    {
        if (_cloudflareWindow is null)
        {
            return false;
        }

        try
        {
            var reused = await _cloudflareWindow.TryReuseSessionAsync();
            if (!reused)
            {
                LogInfo("cloudflare", $"{reason}：浏览器会话暂不可用");
                return false;
            }

            await SyncVerifiedSessionAsync();
            LogInfo("cloudflare", $"{reason}：已自动恢复 Cloudflare 会话，无需手动验证");
            return true;
        }
        catch (Exception ex)
        {
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
        _httpClient = new HanimeHttpClientFactory().Create(cookies, _settings.SiteHost, browserVersion);
        _apiClient = new HanimeApiClient(_cloudflareWindow!, _httpClient, _settings.SiteHost);
        _downloadService = new DownloadService(_httpClient, _settings.SiteHost, CreateDownloadRetryPolicy());

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

    private async Task SyncCachedCookiesToBrowserAsync(IReadOnlyList<BrowserCookieRecord> cookies)
    {
        if (cookies.Count == 0)
        {
            return;
        }

        try
        {
            _cloudflareWindow ??= new CloudflareWindow(_settings.SiteHost) { Owner = this };
            await _cloudflareWindow.ImportCookiesAsync(cookies);
            _appState.Cookies = _cloudflareWindow.Cookies.ToList();
            _appState.CookieHeader = _cloudflareWindow.CookieHeader;
            _appState.BrowserVersion = _cloudflareWindow.BrowserVersion;
            InitSessionWithoutCf(_appState.Cookies, _appState.BrowserVersion);
            LogInfo("cloudflare", $"已同步 {_settings.SiteHost} 的缓存 Cookie 到浏览器会话，共 {_appState.Cookies.Count} 个");
        }
        catch (Exception ex)
        {
            LogError("cloudflare", $"同步 {_settings.SiteHost} 的缓存 Cookie 到浏览器会话失败", ex);
        }
    }

    private async Task RefreshQueueUrlsAfterSessionRestoreAsync()
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

        StatusText.Text = $"正在为 {distinctItems.Count} 个队列项重新获取下载链接...";
        var refreshed = 0;
        var refreshGate = new SemaphoreSlim(2);
        var tasks = distinctItems.Select(async item =>
        {
            await refreshGate.WaitAsync();
            try
            {
                var match = await ResolveQueueItemSourceAsync(item);
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
        StatusText.Text = $"已为 {refreshed}/{distinctItems.Count} 个队列项刷新下载链接。";
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

    private async Task<bool> EnsureVerifiedSessionAsync(string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operationId = CreateOperationId("cfrecover");
        if (_sessionRecoveryTask is not null)
        {
            LogInfoThrottled("cloudflare", $"[{operationId}] 复用进行中的会话恢复任务", TimeSpan.FromSeconds(5));
            return await _sessionRecoveryTask.WaitAsync(cancellationToken);
        }

        var recoveryTask = EnsureVerifiedSessionCoreAsync(reason, operationId, cancellationToken);
        _sessionRecoveryTask = recoveryTask;
        try
        {
            return await recoveryTask.WaitAsync(cancellationToken);
        }
        finally
        {
            if (ReferenceEquals(_sessionRecoveryTask, recoveryTask))
            {
                _sessionRecoveryTask = null;
            }
        }
    }

    private async Task<bool> EnsureVerifiedSessionCoreAsync(string reason, string operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cloudflareWindow ??= new CloudflareWindow(_settings.SiteHost) { Owner = this };
        StatusText.Text = $"{reason} 正在后台自动恢复会话，无需手动操作。";
        LogInfo("cloudflare", $"[{operationId}] {reason}");
        var verified = await _cloudflareWindow.VerifyAsync(forceRefresh: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!verified || _cloudflareWindow.Cookies.Count == 0 || string.IsNullOrWhiteSpace(_cloudflareWindow.CookieHeader))
        {
            StatusText.Text = "Cloudflare 会话恢复未完成，请回到验证窗口完成站点验证。";
            LogInfo("cloudflare", $"[{operationId}] Cloudflare 会话恢复未完成");
            return false;
        }

        await SyncVerifiedSessionAsync();
        cancellationToken.ThrowIfCancellationRequested();
        StatusText.Text = $"Cloudflare 会话已恢复，当前共 {_appState.Cookies.Count} 个 Cookie。";
        LogInfo("cloudflare", $"[{operationId}] Cloudflare 会话已恢复，Cookie 数量 {_appState.Cookies.Count}");
        await RefreshQueueUrlsAfterSessionRestoreAsync();
        return true;
    }
}
