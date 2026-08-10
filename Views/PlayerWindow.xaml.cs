using Hanime1Downloader.CSharp.Models;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

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
    private bool _isBrowserReady;
    private bool _webResourceHandlerAttached;
    private string _currentVideoUrl = string.Empty;

    public PlayerWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        ShowActivated = true;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public async Task OpenAsync(string title, string videoUrl, string type)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "播放" : $"播放 - {title}";
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "正在加载播放器..." : title;
        _currentVideoUrl = videoUrl;
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
        }

        var page = PlayerPageBuilder.Build(title, videoUrl, type, HlsScript.Value);
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
            e.Request.Headers.SetHeader("Referer", $"https://{_settings.SiteHost.Trim().TrimEnd('/')}/");
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
        if (state.Left.HasValue && state.Top.HasValue && double.IsFinite(state.Left.Value) && double.IsFinite(state.Top.Value))
        {
            Left = state.Left.Value;
            Top = state.Top.Value;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        WindowState = state.WindowState;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
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

        StopPlayback();
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

    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr hMem);

    private void CopyLinkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentVideoUrl))
            return;

        // retry up to 10 times in case clipboard is briefly locked
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var bytes = System.Text.Encoding.Unicode.GetBytes(_currentVideoUrl + "\0");
                    var hMem = GlobalAlloc(0x0042 /* GMEM_MOVEABLE|GMEM_ZEROINIT */, (UIntPtr)bytes.Length);
                    var ptr = GlobalLock(hMem);
                    Marshal.Copy(bytes, 0, ptr, bytes.Length);
                    GlobalUnlock(hMem);
                    SetClipboardData(13 /* CF_UNICODETEXT */, hMem);
                }
                finally
                {
                    CloseClipboard();
                }
                return;
            }
            System.Threading.Thread.Sleep(10);
        }
    }
}

