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
        Closed += (_, _) => _pollTimer.Stop();
        _pollTimer.Tick += async (_, _) => await CheckVerificationStateAsync();
    }

    public async Task<bool> VerifyAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();
        _verificationCompletionSource?.TrySetResult(false);
        _verificationCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _autoCompleteWhenReady = true;

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
            var navigation = await navigationCompletionSource.Task;

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
        var minimumWait = TimeSpan.FromMilliseconds(isWatchPage || isSearchPage ? 35 : 15);
        var maximumWait = isWatchPage
            ? TimeSpan.FromMilliseconds(260)
            : isSearchPage
                ? TimeSpan.FromMilliseconds(180)
                : TimeSpan.FromMilliseconds(60);
        var startedAt = DateTime.UtcNow;

        while (true)
        {
            var payload = await Browser.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({ ready: document.readyState, bodyLength: document.body ? document.body.innerHTML.length : 0, relatedContainers: document.querySelectorAll('[class*=\"related\"], [class*=\"recommend\"]').length, relatedLinks: document.querySelectorAll('[class*=\"related\"] a[href], [class*=\"recommend\"] a[href]').length, resultLinks: document.querySelectorAll('.content-padding-new a[href], .home-rows-videos-wrapper a[href]').length })");
            var state = DeserializeScriptResult<PageReadiness>(payload) ?? new PageReadiness();
            var elapsed = DateTime.UtcNow - startedAt;
            var pageReady = string.Equals(state.Ready, "complete", StringComparison.OrdinalIgnoreCase) && state.BodyLength > 0;
            var relatedReady = state.RelatedContainers == 0 || state.RelatedLinks > 0;
            var resultReady = state.ResultLinks > 0;

            if (pageReady && elapsed >= minimumWait &&
                (!isWatchPage || relatedReady || elapsed >= maximumWait) &&
                (!isSearchPage || resultReady || elapsed >= maximumWait))
            {
                return;
            }

            if (elapsed >= maximumWait)
            {
                return;
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        await InitializeBrowserAsync();
        Browser.CoreWebView2.Navigate(_siteBaseUrl);
    }

    public async Task<bool> TryReuseSessionAsync()
    {
        await EnsureInitializedAsync();
        if (Browser.CoreWebView2 is null)
        {
            return false;
        }

        var navigationCompletionSource = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void HandleNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            navigationCompletionSource.TrySetResult(args);
        }

        Browser.CoreWebView2.NavigationCompleted += HandleNavigationCompleted;
        try
        {
            Browser.CoreWebView2.Navigate(_siteBaseUrl);
            var navigation = await navigationCompletionSource.Task;
            if (!navigation.IsSuccess && navigation.WebErrorStatus != CoreWebView2WebErrorStatus.Unknown)
            {
                return false;
            }

            await Task.Delay(250);
            var payload = await Browser.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({ html: document.documentElement ? document.documentElement.outerHTML : '', ready: document.readyState, href: location.href })");
            var state = DeserializeScriptResult<PageState>(payload) ?? new PageState();
            if (IsChallengePage(state.Html ?? string.Empty))
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

        await _initializedCompletionSource.Task;
    }

    private async Task InitializeBrowserAsync()
    {
        Directory.CreateDirectory(WebViewUserDataFolder);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: WebViewUserDataFolder);
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

        await CheckVerificationStateAsync();
    }

    private async Task CheckVerificationStateAsync()
    {
        if (Browser.CoreWebView2 is null || _isCheckingState) return;
        _isCheckingState = true;
        try
        {
        var payload = await Browser.CoreWebView2.ExecuteScriptAsync(
            "JSON.stringify({ html: document.documentElement?.outerHTML ?? '', title: document.title, ready: document.readyState, href: location.href, bodyText: document.body?.innerText ?? '' })");
        var state = DeserializeScriptResult<PageState>(payload) ?? new PageState();
        var html = state.Html ?? string.Empty;

        var challengePresent = IsChallengePage(html);
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

        return !IsChallengePage(state.Html ?? string.Empty);
    }

    private static bool IsChallengePage(string html)
    {
        return html.Contains("Performing security verification", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("challenge-form", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("security verification", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("window._cf_chl_opt", StringComparison.OrdinalIgnoreCase);
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
        public int RelatedContainers { get; set; }
        public int RelatedLinks { get; set; }
        public int ResultLinks { get; set; }
    }
}
