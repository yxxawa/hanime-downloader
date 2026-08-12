using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Hanime1Downloader.CSharp.Views;

public partial class CloudflareWindow : Window
{
    private static readonly JsonSerializerOptions ScriptJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private string WebViewUserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hanime1Downloader.CSharp",
        "WebView2",
        _siteHost);

    private static readonly TimeSpan PagePollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SearchWatchMaxWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OtherPagesMaxWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ContentRenderGrace = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan SessionReuseMaxWait = TimeSpan.FromSeconds(15);

    private bool _isCheckingState;

    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly CookieSessionBridge _cookieBridge;
    private readonly string _siteHost;
    private readonly string _siteBaseUrl;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly TaskCompletionSource<bool> _initializedCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? _verificationCompletionSource;
    private bool _autoCompleteWhenReady;
    private bool _initialized;

    public string CookieHeader { get; private set; } = string.Empty;
    public string BrowserVersion { get; private set; } = string.Empty;
    public IReadOnlyList<BrowserCookieRecord> Cookies { get; private set; } = [];

    public CloudflareWindow(string siteHost = "hanime1.me")
    {
        _siteHost = siteHost;
        _siteBaseUrl = $"https://{siteHost}/";
        _cookieBridge = new CookieSessionBridge(siteHost);
        InitializeComponent();
        StatusText.Text = $"请在内置浏览器中完成 {siteHost} 的 Cloudflare 验证。";
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _pollTimer.Stop();
            try
            {
                Browser?.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Info("cloudflare", $"WebView2 释放失败: {ex.Message}");
            }
        };
        _pollTimer.Tick += async (_, _) =>
        {
            try
            {
                await CheckVerificationStateAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Info("cloudflare", $"验证状态轮询异常: {ex.Message}");
            }
        };
    }

    public async Task<bool> VerifyAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();
        cancellationToken.ThrowIfCancellationRequested();
        _verificationCompletionSource?.TrySetResult(false);
        _verificationCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _autoCompleteWhenReady = true;

        // 用户取消（暂停等场景）时停止等待验证，不再无限挂起。
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            _pollTimer.Stop();
            _verificationCompletionSource?.TrySetResult(false);
        });

        if (!IsVisible)
        {
            Show();
        }

        Activate();
        if (Browser.CoreWebView2 is not null)
        {
            if (forceRefresh)
            {
                await ClearHanimeCookiesAsync();
            }
            else
            {
                FinishButton.IsEnabled = false;
                StatusText.Text = "正在打开首页，进入站点后会自动保存 Cookie。";
            }

            Browser.CoreWebView2.Navigate(_siteBaseUrl);
            _pollTimer.Start();
        }

        return await _verificationCompletionSource.Task;
    }

    public async Task ImportCookiesAsync(IReadOnlyList<BrowserCookieRecord> cookies)
    {
        await EnsureInitializedAsync();
        if (Browser.CoreWebView2 is null)
        {
            throw new InvalidOperationException("浏览器上下文尚未初始化，请先完成验证。");
        }

        await ClearHanimeCookiesAsync();
        foreach (var record in cookies.Where(record => !string.IsNullOrWhiteSpace(record.Name) && !string.IsNullOrWhiteSpace(record.Value)))
        {
            var cookie = Browser.CoreWebView2.CookieManager.CreateCookie(
                record.Name,
                record.Value,
                string.IsNullOrWhiteSpace(record.Domain) ? $".{_siteHost}" : record.Domain,
                string.IsNullOrWhiteSpace(record.Path) ? "/" : record.Path);
            cookie.IsHttpOnly = record.IsHttpOnly;
            cookie.IsSecure = record.IsSecure;
            if (record.Expires is double expiresUnixSeconds)
            {
                try
                {
                    cookie.Expires = DateTimeOffset.FromUnixTimeSeconds((long)expiresUnixSeconds).UtcDateTime;
                }
                catch
                {
                    // 过期时间异常时保持会话 Cookie。
                }
            }
            Browser.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
        }

        Cookies = await _cookieBridge.ExportCookiesAsync(Browser.CoreWebView2.CookieManager);
        CookieHeader = _cookieBridge.BuildCookieHeader(Cookies);
        FinishButton.IsEnabled = CookieHeader.Contains("cf_clearance=", StringComparison.OrdinalIgnoreCase);
        StatusText.Text = FinishButton.IsEnabled ? "已导入 Cookie，请刷新或直接继续使用。" : "已导入 Cookie，但未检测到 cf_clearance。";
    }

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
                "JSON.stringify({ status: document.documentElement ? 200 : 0, url: location.href, title: document.title, html: document.documentElement ? document.documentElement.outerHTML : '', headers: {} })");
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
                if (!string.Equals(state.Visibility, lastVisibility, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Info("cloudflare", $"挑战等待中 visibility={state.Visibility}, focused={state.Focused}, IsVisible={IsVisible}, shown={shownForChallenge}");
                    lastVisibility = state.Visibility ?? string.Empty;
                }

                // 托管挑战会自动通过并跳转到真实页面，继续等待。
                // 实测结论：挑战脚本需要在屏幕内可见且获得焦点的窗口里才能完成
                // （隐藏/屏幕外/无焦点均卡住，弹验证窗口后约 2 秒自动通过）。
                // 统一使用与手动验证一致的居中完整窗口（CenterOwner），挑战通过后自动隐藏。
                if (!shownForChallenge)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsActiveVerification() && !IsVisible)
                        {
                            try
                            {
                                ShowInTaskbar = true;
                                WindowStyle = WindowStyle.SingleBorderWindow;
                                Topmost = false;
                                Width = 520;
                                Height = 480;
                                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                                StatusText.Text = "检测到托管挑战，正在自动验证，请保持窗口打开...";
                                Show();
                                Activate();
                                shownForChallenge = true;

                                // 关键修复：当前页面是在窗口隐藏状态下加载的，Chromium 的渲染节流
                                // 导致挑战脚本初始化不完整（窗口内容空白、挑战永远不通过）。
                                // 窗口可见后重新加载当前页，让挑战在可见+聚焦的环境中从头运行。
                                if (Browser.CoreWebView2 is not null)
                                {
                                    Browser.CoreWebView2.Reload();
                                }
                            }
                            catch
                            {
                                // 显示失败时保持隐藏，等待逻辑仍有上限兜底。
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        try
        {
            await InitializeBrowserAsync();
        }
        catch (Exception ex)
        {
            // 初始化失败（如 WebView2 运行时缺失）：必须释放等待者，否则所有 await 永久挂起。
            AppLogger.Error("cloudflare", "WebView2 初始化失败", ex);
            _initialized = true;
            _initializedCompletionSource.TrySetResult(true);
            return;
        }
        // 不再在此预导航主页：VerifyAsync / TryReuseSessionAsync 都会自行导航，
        // 启动时立即导航一次只会造成重复的完整主页加载。
    }

    public async Task<bool> TryReuseSessionAsync()
    {
        await EnsureInitializedAsync();
        if (Browser.CoreWebView2 is null)
        {
            return false;
        }

        await _fetchLock.WaitAsync();
        var navigationCompletionSource = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void HandleNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            navigationCompletionSource.TrySetResult(args);
        }

        Browser.CoreWebView2.NavigationCompleted += HandleNavigationCompleted;
        try
        {
            Browser.CoreWebView2.Navigate(_siteBaseUrl);
            var navigation = await navigationCompletionSource.Task.WaitAsync(TimeSpan.FromSeconds(45), CancellationToken.None);
            if (!navigation.IsSuccess && navigation.WebErrorStatus != CoreWebView2WebErrorStatus.Unknown)
            {
                return false;
            }

            // 等待托管挑战自动通过（若有），或主页内容就绪。
            await WaitForPageReadyAsync("/", false, false, SessionReuseMaxWait, CancellationToken.None);
            var payload = await Browser.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({ html: document.documentElement ? document.documentElement.outerHTML : '', ready: document.readyState, href: location.href, title: document.title })");
            var state = DeserializeScriptResult<PageState>(payload) ?? new PageState();
            if (CloudflareDetection.IsChallengePage(state.Html ?? string.Empty, state.Title))
            {
                return false;
            }

            Cookies = await _cookieBridge.ExportCookiesAsync(Browser.CoreWebView2.CookieManager);
            CookieHeader = _cookieBridge.BuildCookieHeader(Cookies);
            FinishButton.IsEnabled = CookieHeader.Contains("cf_clearance=", StringComparison.OrdinalIgnoreCase);
            return Cookies.Any(cookie => cookie.Name.Equals("cf_clearance", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(cookie.Value));
        }
        finally
        {
            Browser.CoreWebView2.NavigationCompleted -= HandleNavigationCompleted;
            _fetchLock.Release();
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }

        if (!IsLoaded)
        {
            Show();
            Hide();
        }

        // 初始化结果无论成败都必须完成（OnLoaded/InitializeBrowserAsync 的 catch 保证），
        // 超时保护防止极端情况下永久挂起。
        await _initializedCompletionSource.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private async Task InitializeBrowserAsync()
    {
        // WebView2 运行时缺失检测：不检测则 CreateAsync 抛异常且无用户可见提示。
        var availableVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        if (string.IsNullOrWhiteSpace(availableVersion))
        {
            throw new InvalidOperationException("未检测到 WebView2 运行时，请安装 Microsoft Edge WebView2 Runtime。");
        }

        Directory.CreateDirectory(WebViewUserDataFolder);
        var environmentOptions = new CoreWebView2EnvironmentOptions(
            // 关闭 Chromium 对隐藏/遮挡窗口的节流（background timer throttling 与 occlusion detection），
            // 隐藏窗口中的 Cloudflare 托管挑战 JS 才能全速运行并自动通过。
            "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-features=CalculateNativeWinOcclusion");
        var environment = await CoreWebView2Environment.CreateAsync(
            userDataFolder: WebViewUserDataFolder,
            options: environmentOptions);
        await Browser.EnsureCoreWebView2Async(environment);
        Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
        Browser.CoreWebView2.Settings.IsZoomControlEnabled = false;
        Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        Browser.CoreWebView2.Settings.IsSwipeNavigationEnabled = true;
        BrowserVersion = Browser.CoreWebView2.Environment.BrowserVersionString;
        Browser.CoreWebView2.Settings.UserAgent = BrowserIdentity.BuildUserAgent(BrowserVersion);
        Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        _initialized = true;
        _initializedCompletionSource.TrySetResult(true);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_verificationCompletionSource is not null && !_verificationCompletionSource.Task.IsCompleted)
        {
            e.Cancel = true;
            _verificationCompletionSource.TrySetResult(false);
            Hide();
            return;
        }
        _initialized = false;
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

    private async Task ClearHanimeCookiesAsync()
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(_siteBaseUrl);
        foreach (var cookie in cookies)
        {
            Browser.CoreWebView2.CookieManager.DeleteCookie(cookie);
        }

        Cookies = [];
        CookieHeader = string.Empty;
        FinishButton.IsEnabled = false;
        StatusText.Text = "已清理旧 Cookie，请在页面中重新完成 Cloudflare 验证。";
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

    private bool IsHomePageReady(PageState state)
    {
        var href = state.Href ?? string.Empty;
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Host, _siteHost, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, $"www.{_siteHost}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!string.Equals(state.Ready, "complete", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !CloudflareDetection.IsChallengePage(state.Html ?? string.Empty, state.Title);
    }

    private static T? DeserializeScriptResult<T>(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload == "null" || payload == "undefined")
        {
            return default;
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.String)
        {
            var innerJson = root.GetString();
            return string.IsNullOrWhiteSpace(innerJson) ? default : JsonSerializer.Deserialize<T>(innerJson, ScriptJsonOptions);
        }

        return JsonSerializer.Deserialize<T>(root.GetRawText(), ScriptJsonOptions);
    }

    private sealed class PageState
    {
        public string? Html { get; set; }
        public string? Title { get; set; }
        public string? Ready { get; set; }
        public string? Href { get; set; }
        public string? BodyText { get; set; }
    }

    private sealed class PageReadiness
    {
        public string? Ready { get; set; }
        public int BodyLength { get; set; }
        public bool Challenge { get; set; }
        public string? Visibility { get; set; }
        public bool Focused { get; set; }
        public int ResultLinks { get; set; }
    }
}
