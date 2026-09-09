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
    /// <summary>后台静默通过托管挑战的最长等待；超过后才会显示窗口交给用户（交互式验证兜底）。</summary>
    private static readonly TimeSpan HiddenChallengeGrace = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ClearancePollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ScriptFetchTimeout = TimeSpan.FromSeconds(10);

    private bool _isCheckingState;

    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly CookieSessionBridge _cookieBridge;
    private readonly string _siteHost;
    private readonly string _siteBaseUrl;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    /// <summary>页内 fetch 的 postMessage 回传等待器（key = slot），避免整页 HTML 走 ExecuteScriptAsync 二次编码。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ScriptFetchPayload>> _scriptFetchWaiters = new(StringComparer.Ordinal);
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

    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }

        if (!IsLoaded)
        {
            // WebView2 必须进入可视化树才能初始化；把窗口放到屏幕外再 Show，
            // 避免启动时在桌面上闪出一个验证窗口。
            var savedStartupLocation = WindowStartupLocation;
            var savedLeft = Left;
            var savedTop = Top;
            try
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -32000;
                Top = -32000;
                Show();
            }
            finally
            {
                Hide();
                Left = savedLeft;
                Top = savedTop;
                WindowStartupLocation = savedStartupLocation;
            }
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
        Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
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

    private sealed class ScriptFetchPayload
    {
        public string? Slot { get; set; }
        public int Status { get; set; }
        public string? Url { get; set; }
        public string? Html { get; set; }
        public string? Error { get; set; }
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
