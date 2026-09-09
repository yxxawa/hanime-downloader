using Hanime1Downloader.CSharp.Models;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Hanime1Downloader.CSharp.Views;

public partial class PlayerWindow : Window
{
    private static readonly string WebViewUserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hanime1Downloader.CSharp",
        "WebView2",
        "player");
    private static readonly Lazy<string> HlsScript = new(LoadHlsScript);

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _positionSaveTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _isBrowserReady;
    private bool _webResourceHandlerAttached;
    private string _currentVideoUrl = string.Empty;
    private string _videoId = string.Empty;
    private string _referer = string.Empty;

    public PlayerWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        _positionSaveTimer.Tick += (_, _) => _ = SavePlaybackPositionAsync();
        ShowActivated = true;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public async Task OpenAsync(string title, string videoUrl, string type, string? videoId = null)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "播放" : $"播放 - {title}";
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "正在加载播放器..." : title;
        _currentVideoUrl = videoUrl;
        _videoId = videoId ?? string.Empty;
        Show();
        Activate();
        Focus();

        if (!_isBrowserReady)
        {
            Directory.CreateDirectory(WebViewUserDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: WebViewUserDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            Browser.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
            _webResourceHandlerAttached = true;
            _isBrowserReady = true;
            // hls.min.js 只在文档创建时注入一次：旧实现每次播放都把 543KB 脚本 JSON 转义后塞进 HTML 再 eval。
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HlsScript.Value);
            _referer = $"https://{_settings.SiteHost.Trim().TrimEnd('/')}/";
        }

        double? restoredPosition = null;
        if (!string.IsNullOrWhiteSpace(_videoId) &&
            _settings.PlayerWindow.PlaybackPositions.TryGetValue(_videoId, out var savedPosition) &&
            savedPosition > 3)
        {
            restoredPosition = savedPosition;
        }

        var page = PlayerPageBuilder.Build(title, videoUrl, type, restoredPosition, _settings.PlayerWindow.Volume);
        Browser.NavigateToString(page);
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "播放" : title;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentVideoUrl) ||
            !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var requestedUri) ||
            (!string.Equals(requestedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(requestedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_referer))
            {
                e.Request.Headers.SetHeader("Referer", _referer);
            }
        }
        catch
        {
        }
    }

    private static string LoadHlsScript()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/hls.min.js", UriKind.Absolute));
            if (resource is null)
            {
                return string.Empty;
            }

            using var stream = resource.Stream;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var state = _settings.PlayerWindow;
        Width = state.Width > MinWidth ? state.Width : Width;
        Height = state.Height > MinHeight ? state.Height : Height;
        // 恢复位置前做屏幕边界校验：显示器变更后不在可见范围则保持居中。
        if (state.Left.HasValue && state.Top.HasValue && double.IsFinite(state.Left.Value) && double.IsFinite(state.Top.Value))
        {
            var virtualBounds = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
            if (virtualBounds.Contains(new Point(state.Left.Value + 50, state.Top.Value + 20)))
            {
                Left = state.Left.Value;
                Top = state.Top.Value;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }
        WindowState = state.WindowState;
        // 播放中定时快照进度：关闭瞬间的脚本读取可能与 StopPlayback 竞争，
        // 有定时快照后最多只丢 5 秒，且每个视频各记各的。
        _positionSaveTimer.Start();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _positionSaveTimer.Stop();
        var state = _settings.PlayerWindow;
        state.WindowState = Enum.IsDefined(WindowState) ? WindowState : WindowState.Normal;

        var width = WindowState == WindowState.Normal ? Width : RestoreBounds.Width;
        var height = WindowState == WindowState.Normal ? Height : RestoreBounds.Height;
        var left = WindowState == WindowState.Normal ? Left : RestoreBounds.Left;
        var top = WindowState == WindowState.Normal ? Top : RestoreBounds.Top;

        state.Width = double.IsFinite(width) && width > 0 ? width : 920;
        state.Height = double.IsFinite(height) && height > 0 ? height : 620;
        state.Left = double.IsFinite(left) ? left : null;
        state.Top = double.IsFinite(top) ? top : null;

        _ = SavePlaybackPositionAsync();
        StopPlayback();
    }

    /// <summary>从播放页取回播放位置与音量（fire-and-forget，尽量保存）。</summary>
    private async Task SavePlaybackPositionAsync()
    {
        if (!_isBrowserReady || Browser.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var result = await Browser.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({ position: document.querySelector('video') ? document.querySelector('video').currentTime || 0 : 0, duration: document.querySelector('video') ? document.querySelector('video').duration || 0 : 0, volume: document.querySelector('video') ? document.querySelector('video').volume : null })");
            if (string.IsNullOrWhiteSpace(result))
            {
                return;
            }

            var payload = result.Trim('"').Replace("\\\"", "\"");
            var doc = System.Text.Json.JsonDocument.Parse(payload);
            var state = _settings.PlayerWindow;
            var seconds = doc.RootElement.TryGetProperty("position", out var position) && position.TryGetDouble(out var parsedPosition)
                ? parsedPosition
                : 0d;
            var duration = doc.RootElement.TryGetProperty("duration", out var durationElement) && durationElement.TryGetDouble(out var parsedDuration)
                ? parsedDuration
                : 0d;

            if (!string.IsNullOrWhiteSpace(_videoId))
            {
                // 播到接近结尾视为已看完：移除记录，下次从头播放。
                if (seconds > 3 && (duration <= 0 || seconds < duration - 10))
                {
                    state.PlaybackPositions[_videoId] = seconds;
                    TrimPlaybackPositions(state);
                }
                else
                {
                    state.PlaybackPositions.Remove(_videoId);
                }
            }

            if (doc.RootElement.TryGetProperty("volume", out var volume) && volume.TryGetDouble(out var vol) && vol >= 0)
            {
                state.Volume = vol;
            }
        }
        catch
        {
        }
    }

    /// <summary>播放进度表只保留最近 500 条，避免无限增长。</summary>
    private static void TrimPlaybackPositions(PlayerWindowSettings state)
    {
        const int maxEntries = 500;
        while (state.PlaybackPositions.Count > maxEntries)
        {
            var oldest = state.PlaybackPositions.Keys.FirstOrDefault();
            if (oldest is null)
            {
                return;
            }

            state.PlaybackPositions.Remove(oldest);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!_isBrowserReady)
        {
            return;
        }

        try
        {
            if (_webResourceHandlerAttached)
            {
                Browser.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
                _webResourceHandlerAttached = false;
            }
            Browser.Dispose();
        }
        catch
        {
        }
    }

    private void StopPlayback()
    {
        if (!_isBrowserReady)
        {
            return;
        }

        try
        {
            if (Browser.CoreWebView2 is not null)
            {
                _ = Browser.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('video,audio').forEach(el => { try { el.pause(); el.removeAttribute('src'); if (typeof el.load === 'function') { el.load(); } } catch {} }); if (window.__hanimeHls) { try { window.__hanimeHls.destroy(); } catch {} window.__hanimeHls = null; } if (document.body) { document.body.innerHTML = ''; }");
                Browser.CoreWebView2.Stop();
            }

            _currentVideoUrl = string.Empty;
            Browser.NavigateToString("<!DOCTYPE html><html><body style=\"margin:0;background:#000;\"></body></html>");
        }
        catch
        {
        }
    }

    private async void CopyLinkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentVideoUrl))
        {
            return;
        }

        // 剪贴板可能被其他进程短暂占用，重试几次。
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(_currentVideoUrl, true);
                return;
            }
            catch
            {
                await Task.Delay(10);
            }
        }
    }
}

