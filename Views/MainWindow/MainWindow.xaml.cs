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

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string DefaultFavoritesFolder = "默认收藏夹";
    private static readonly string AppDataDir = AppPaths.DataDirectory;
    private static readonly string FavoritesFilePath = AppPaths.FavoritesFile;
    private static readonly string DownloadHistoryFilePath = AppPaths.DownloadHistoryFile;
    private static readonly string DownloadQueueFilePath = AppPaths.DownloadQueueFile;
    private static readonly string LegacyCookieCacheFilePath = AppPaths.LegacyCookieCacheFile;
    private static readonly string SettingsFilePath = AppPaths.SettingsFile;
    private static readonly JsonSerializerOptions FavoritesJsonOptions = new() { WriteIndented = true };

    private readonly AppState _appState = new();
    private readonly AppSettings _settings = new();
    private readonly SearchFilterOptions _searchFilters = new();
    private ObservableCollection<VideoSummary> _searchResults = [];
    private long _lastQueueSummaryUiTick;
    private readonly ObservableCollection<DownloadHistoryItem> _historyItems = [];
    private readonly HashSet<string> _historyVideoIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HttpClient> _retiredHttpClients = [];
    private readonly ObservableCollection<DownloadQueueItem> _downloadQueue = [];
    private readonly Dictionary<string, ObservableCollection<VideoSummary>> _favoriteFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VideoDetails> _videoDetailsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _videoDetailsCacheOrder = new();
    private const int VideoDetailsCacheMaxEntries = 200;
    private long _historyFileStateRefreshedAt;
    private readonly Dictionary<string, Task<VideoDetails?>> _videoDetailsInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _favoritesFilterTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _historyFilterTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private ICollectionView? _favoritesView;
    private ICollectionView? _historyView;
    private CloudflareWindow? _cloudflareWindow;
    private HanimeApiClient? _apiClient;
    private DownloadService? _downloadService;
    private HttpClient? _httpClient;
    private VideoDetails? _currentDetails;
    private string? _currentDetailsVideoId;
    private CancellationTokenSource? _detailsLoadCancellationTokenSource;
    private bool _isSearching;
    private bool _isLoadingDetails;
    private bool _isDownloadingQueue;
    private int _detailsRequestVersion;
    private Task<bool>? _sessionRecoveryTask;
    private bool _isPauseRequested;
    private bool _hasPausedQueue;
    private int _operationSequence;
    private bool _isDragSelecting;
    private bool _dragSelectExtendSelection;
    private bool _queueCtrlSelectMode;
    private ListBox? _dragSelectList;
    private int _dragSelectStartIndex = -1;
    private Point _dragSelectStartPoint;
    private Point _queueDragStartPoint;
    private List<object> _dragSelectInitialItems = [];
    private DownloadQueueItem? _queueCtrlClickedItem;
    private List<DownloadQueueItem>? _queueDragItems;
    private CancellationTokenSource? _downloadQueueCancellationTokenSource;
    private readonly Dictionary<DownloadQueueItem, CancellationTokenSource> _activeQueueItemCancellationTokenSources = [];
    private readonly Dictionary<DownloadQueueItem, CancellationTokenSource> _activeQueueItemLinkedCancellationTokenSources = [];
    private readonly HashSet<DownloadQueueItem> _reResolveItems = [];
    private TaskCompletionSource<bool> _downloadQueueChangedSignal = CreateDownloadQueueChangedSignal();
    private DownloadQueueItem? _currentQueueDownloadItem;
    private QueueRunSummaryState _queueRunSummaryState;
    private int _queueRunTotalCount;
    private int _queueRunCompletedCount;
    private int _queueRunFailedCount;
    private bool _queueRunSelectionOnly;
    private string _queueRunCurrentTitle = string.Empty;
    private double _queueRunCurrentProgress;
    private string _currentSearchKeyword = string.Empty;
    private int _currentPage = 1;
    private int _totalPages = 1;
    private Regex? _cachedVideoLinkRegex;
    private string _cachedVideoLinkRegexHost = string.Empty;
    private readonly List<string> _startupWarnings = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public Visibility ShowListCoversVisibility => _settings.ShowListCovers ? Visibility.Visible : Visibility.Collapsed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "5.0.0";
        Title = $"Hanime1视频工具 v{version}";
        _favoritesFilterTimer.Tick += (_, _) =>
        {
            _favoritesFilterTimer.Stop();
            _favoritesView?.Refresh();
        };
        _historyFilterTimer.Tick += (_, _) =>
        {
            _historyFilterTimer.Stop();
            _historyView?.Refresh();
        };
        InitializeCollections();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void InitializeCollections()
    {
        Directory.CreateDirectory(AppDataDir);
        LoadSettings();
        AppThemeService.Apply(Application.Current, _settings.ThemeMode);
        if (_settings.PersistDownloadQueue)
        {
            LoadDownloadQueue();
        }
        ResultsList.ItemsSource = _searchResults;
        DownloadQueueList.ItemsSource = _downloadQueue;
        PreviewCoverButton.IsEnabled = false;
        QueueSourceButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        ApplyVideoDetailsVisibility();
        UpdatePageNavigationUi();
        UpdateFilterSummaryUi();
        ResetQueueRunSummaryState();
        UpdateDownloadQueueControlUi();
    }

    /// <summary>
    /// 收藏夹与下载历史改到窗口显示后再加载：这两份数据最大（历史可达 2000 条），
    /// 放到首屏渲染之后可以让窗口更快出现。
    /// </summary>
    private void LoadPersistedBrowsingData()
    {
        LoadFavorites();
        LoadDownloadHistory();
        HistoryList.ItemsSource = _historyItems;
        FavoritesFolderBox.ItemsSource = _favoriteFolders.Keys.ToList();
        FavoritesFolderBox.SelectedItem = DefaultFavoritesFolder;
        RefreshFavoritesView();
        RefreshHistoryView();
    }

    private void RestoreWindowBounds()
    {
        // 仅当保存的位置在可见屏幕范围内时恢复（防止显示器变更后窗口出现在屏幕外）。
        var left = _settings.WindowLeft;
        var top = _settings.WindowTop;
        var virtualBounds = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        if (left is double l && top is double t &&
            virtualBounds.Contains(new Point(l + 50, t + 20)))
        {
            Left = l;
            Top = t;
        }

        if (_settings.WindowWidth is double w && w >= 800 && _settings.WindowHeight is double h && h >= 600)
        {
            Width = w;
            Height = h;
        }

        if (_settings.WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowBounds()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        _settings.WindowState = WindowState;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isDownloadingQueue && _downloadQueue.Any(item => item.IsDownloading))
        {
            var result = MessageBox.Show(this, "下载进行中，确定退出？", "退出确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            // 优雅停止：取消队列运行并逐项取消进行中的下载（保留 .tmp 以便重启后续传）。
            _isPauseRequested = true;
            _downloadQueueCancellationTokenSource?.Cancel();
            foreach (var item in _downloadQueue.Where(item => item.IsDownloading))
            {
                CancelQueueItem(item);
            }
        }

        foreach (Window window in Application.Current.Windows)
        {
            if (window != this && window.IsLoaded)
                window.Close();
        }

        SaveWindowBounds();
        SaveSettings();
        _titleCopiedHintCts?.Dispose();
        _titleCopiedHintCts = null;

        if (_settings.PersistDownloadQueue)
        {
            try
            {
                AtomicFile.WriteAllText(DownloadQueueFilePath, JsonSerializer.Serialize(
                    _downloadQueue.Select(item => new DownloadQueueRecord
                    {
                        Title = item.Title,
                        Url = item.Url,
                        Type = item.Type,
                        Quality = item.Quality,
                        VideoId = item.VideoId,
                        TargetPath = item.TargetPath,
                        HasError = item.HasError
                    }), FavoritesJsonOptions));
            }
            catch (Exception ex)
            {
                LogError("queue", "退出时保存下载队列失败", ex);
            }
        }
        else
        {
            foreach (var item in _downloadQueue)
                DeleteQueueItemTemporaryFile(item);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowBounds();
        LoadPersistedBrowsingData();
        _ = CheckForUpdatesOnStartupAsync();
        try
        {
            _cloudflareWindow ??= new CloudflareWindow(_settings.SiteHost) { Owner = this };
            _cloudflareWindow.Closed += (_, _) => _cloudflareWindow = null;

            // 先静默复用浏览器里现存的 Cloudflare 会话（后台通过托管挑战，不弹窗）。
            // 成功就完全不碰磁盘缓存，避免用旧 Cookie 覆盖掉仍然有效的会话。
            var reused = await TryReuseSessionSilentlyAsync("启动时自动恢复会话");
            if (reused)
            {
                ApplyStartupWarnings($"已自动恢复 Cloudflare 会话。当前共 {_appState.Cookies.Count} 个 Cookie。");
                return;
            }

            // 会话确实不可用时，才导入磁盘缓存里的 Cookie 再试一次。
            var cached = LoadCookieCache();
            if (cached.Count > 0)
            {
                try
                {
                    await _cloudflareWindow.ImportCookiesAsync(cached);
                    reused = await TryReuseSessionSilentlyAsync("导入缓存 Cookie 后自动恢复会话");
                }
                catch (Exception ex)
                {
                    LogInfo("cloudflare", $"导入缓存 Cookie 后自动恢复失败: {ex.Message}");
                }
            }

            if (reused)
            {
                ApplyStartupWarnings($"已自动恢复 Cloudflare 会话。当前共 {_appState.Cookies.Count} 个 Cookie。");
                return;
            }

            InitSessionWithoutCf();
            ApplyStartupWarnings("已启动，如遇访问问题请手动验证。");
        }
        catch (Exception ex)
        {
            HandleUiActionError("startup", "初始化失败", ex);
            InitSessionWithoutCf();
        }

        // 孤儿临时文件清理：只删 7 天前且不被当前队列引用的 .tmp/.hls。
        // 引用集合在 UI 线程构建（ObservableCollection 非线程安全），扫描放到后台线程。
        string directory;
        HashSet<string> referenced;
        try
        {
            directory = EnsureDownloadDirectory();
            referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _downloadQueue)
            {
                if (string.IsNullOrWhiteSpace(item.TargetPath))
                {
                    continue;
                }

                try
                {
                    referenced.Add(DownloadPathGuard.EnsureWithinDirectory(directory, item.TargetPath) + ".tmp");
                }
                catch
                {
                    // 路径非法则跳过，不参与引用集合。
                }
            }
        }
        catch (Exception ex)
        {
            LogError("download", "孤儿临时文件清理准备失败", ex);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                CleanupOrphanTemporaryFiles(directory, referenced);
            }
            catch (Exception ex)
            {
                LogError("download", "孤儿临时文件清理失败", ex);
            }
        });
    }

    private void CleanupOrphanTemporaryFiles(string directory, HashSet<string> referenced)
    {
        var cutoff = DateTime.Now.AddDays(-7);
        foreach (var tmp in Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly))
        {
            if (referenced.Contains(tmp))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTime(tmp) < cutoff)
                {
                    File.Delete(tmp);
                    LogInfo("download", $"已清理孤儿临时文件: {tmp}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogInfo("download", $"孤儿临时文件清理跳过: {tmp}; {ex.Message}");
            }
        }

        foreach (var hlsDir in Directory.EnumerateDirectories(directory, "*.hls", SearchOption.TopDirectoryOnly))
        {
            var basePath = hlsDir[..^4];
            if (referenced.Contains(basePath + ".tmp"))
            {
                continue;
            }

            try
            {
                if (Directory.GetLastWriteTime(hlsDir) < cutoff)
                {
                    Directory.Delete(hlsDir, recursive: true);
                    LogInfo("download", $"已清理孤儿 HLS 目录: {hlsDir}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogInfo("download", $"孤儿 HLS 目录清理跳过: {hlsDir}; {ex.Message}");
            }
        }
    }


    private CancellationTokenSource? _titleCopiedHintCts;

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SettingsDialog(_settings) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var oldSiteHost = _settings.SiteHost;
            var oldPersistQueue = _settings.PersistDownloadQueue;
            var oldShowListCovers = _settings.ShowListCovers;
            _settings.DownloadPath = dialog.Settings.DownloadPath;
            _settings.FileNamingRule = dialog.Settings.FileNamingRule;
            _settings.ShowListCovers = dialog.Settings.ShowListCovers;
            _settings.ThemeMode = AppThemeService.Normalize(dialog.Settings.ThemeMode);
            _settings.DefaultQuality = dialog.Settings.DefaultQuality;
            _settings.SiteHost = dialog.Settings.SiteHost;
            _settings.CustomSiteHosts = dialog.Settings.CustomSiteHosts.ToList();
            _settings.PersistDownloadQueue = dialog.Settings.PersistDownloadQueue;
            _settings.MaxConcurrentDownloads = Math.Clamp(dialog.Settings.MaxConcurrentDownloads, 1, 3);
            _settings.MaxRetries = Math.Clamp(dialog.Settings.MaxRetries, 0, 8);
            _settings.VideoDetailsVisibility = dialog.Settings.VideoDetailsVisibility;
            AppThemeService.Apply(Application.Current, _settings.ThemeMode);
            SaveSettings();
            _videoDetailsCache.Clear();
            _videoDetailsInFlight.Clear();
            RefreshFavoritesView();
            ApplyVideoDetailsVisibility();
                OnPropertyChanged(nameof(ShowListCoversVisibility));
            ApplyListCoverSettingChange(oldShowListCovers);

            if (oldSiteHost != _settings.SiteHost)
            {
                _cloudflareWindow?.Close();
                _cloudflareWindow = null;
                _apiClient = null;
                _downloadService = null;

                var cachedCookies = LoadCookieCache().ToList();
                _appState.Cookies = cachedCookies;
                _appState.CookieHeader = string.Join("; ", cachedCookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
                _appState.BrowserVersion = string.Empty;
                InitSessionWithoutCf(cachedCookies, _appState.BrowserVersion);

                if (cachedCookies.Count > 0)
                {
                    _ = SyncCachedCookiesToBrowserAsync(cachedCookies);
                    StatusText.Text = $"站点已切换为 {_settings.SiteHost}，已同步对应站点的 Cookie 缓存。";
                }
                else
                {
                    StatusText.Text = $"站点已切换为 {_settings.SiteHost}，当前站点没有可用 Cookie 缓存。";
                }

                LogInfo("cloudflare", $"站点切换为 {_settings.SiteHost}，读取对应 Cookie 缓存 {cachedCookies.Count} 个");
            }
            else if (oldPersistQueue != _settings.PersistDownloadQueue)
            {
                if (_settings.PersistDownloadQueue)
                {
                    StatusText.Text = "设置已保存，后续会保留下载队列。";
                }
                else
                {
                    StatusText.Text = "设置已保存，关闭程序后将不再保留下载队列。";
                }
            }
            else
            {
                StatusText.Text = "设置已保存。";
            }
        }
        catch (Exception ex)
        {
            HandleUiActionError("settings", "保存设置失败", ex);
        }
    }

    private void FavoritesList_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowVideoListContextMenu(FavoritesList, e.GetPosition(FavoritesList), isFavoriteList: true);
    }

    private void LoadSettings()
    {
        if (!File.Exists(SettingsFilePath))
        {
            SaveSettings();
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFilePath));
            if (loaded is null)
            {
                return;
            }

            _settings.DownloadPath = ResolveDownloadPath(loaded.DownloadPath);
            _settings.FileNamingRule = string.IsNullOrWhiteSpace(loaded.FileNamingRule) ? _settings.FileNamingRule : loaded.FileNamingRule;
            _settings.ShowListCovers = loaded.ShowListCovers;
            _settings.DefaultQuality = loaded.DefaultQuality is "highest" or "lowest" or "720" or "480"
                ? loaded.DefaultQuality
                : _settings.DefaultQuality;
            _settings.ThemeMode = AppThemeService.Normalize(loaded.ThemeMode);
            _settings.SiteHost = string.IsNullOrWhiteSpace(loaded.SiteHost) ? _settings.SiteHost : loaded.SiteHost;
            _settings.CustomSiteHosts = loaded.CustomSiteHosts?.Where(host => !string.IsNullOrWhiteSpace(host)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
            _settings.PersistDownloadQueue = loaded.PersistDownloadQueue;
            _settings.MaxConcurrentDownloads = Math.Clamp(loaded.MaxConcurrentDownloads, 1, 3);
            _settings.MaxRetries = Math.Clamp(loaded.MaxRetries, 0, 8);
            if (string.IsNullOrWhiteSpace(_settings.FileNamingRule) || !_settings.FileNamingRule.Contains('{'))
            {
                _settings.FileNamingRule = "{title}_{videoId}";
                AddStartupWarning("文件命名规则不含占位符，已重置为 {title}_{videoId}");
            }
            _settings.VideoDetailsVisibility = loaded.VideoDetailsVisibility ?? _settings.VideoDetailsVisibility;
            _settings.PlayerWindow = loaded.PlayerWindow ?? _settings.PlayerWindow;
        }
        catch (Exception ex)
        {
            LogError("startup", $"读取设置失败: {SettingsFilePath}", ex);
            AddStartupWarning("设置读取失败，已使用默认设置");
        }
    }

    private static string ResolveDownloadPath(string? configuredPath)
    {
        var normalized = string.IsNullOrWhiteSpace(configuredPath) ? string.Empty : configuredPath.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return AppSettings.DefaultDownloadPath;
        }

        try
        {
            var fullPath = Path.GetFullPath(normalized);
            var legacyAppDataPath = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hanime1Downloader", "Downloads"));
            if (string.Equals(fullPath, legacyAppDataPath, StringComparison.OrdinalIgnoreCase))
            {
                return AppSettings.DefaultDownloadPath;
            }

            // 中间版本曾把默认下载目录放到 %LOCALAPPDATA%\Hanime1Downloader.CSharp\Downloads，
            // 现在默认改为 exe 目录\Downloads，旧值一并视为默认。
            var legacyLocalAppDataPath = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppPaths.AppFolderName,
                "Downloads"));
            if (string.Equals(fullPath, legacyLocalAppDataPath, StringComparison.OrdinalIgnoreCase))
            {
                return AppSettings.DefaultDownloadPath;
            }

            return fullPath;
        }
        catch
        {
            return AppSettings.DefaultDownloadPath;
        }
    }

    private string GetCookieCacheFilePath()
    {
        return AppPaths.CookieCacheFile(_settings.SiteHost);
    }

    private async Task SaveCookieCacheAsync()
    {
        await AtomicFile.WriteAllTextAsync(GetCookieCacheFilePath(), JsonSerializer.Serialize(_appState.Cookies, FavoritesJsonOptions));
    }

    private IReadOnlyList<BrowserCookieRecord> LoadCookieCache()
    {
        var sitePath = GetCookieCacheFilePath();
        if (File.Exists(sitePath))
        {
            try
            {
                return JsonSerializer.Deserialize<List<BrowserCookieRecord>>(File.ReadAllText(sitePath)) ?? [];
            }
            catch (Exception ex)
            {
                LogError("startup", $"读取 Cookie 缓存失败: {sitePath}", ex);
                AddStartupWarning("Cookie 缓存读取失败，已忽略当前站点缓存");
                return [];
            }
        }

        if (!File.Exists(LegacyCookieCacheFilePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<BrowserCookieRecord>>(File.ReadAllText(LegacyCookieCacheFilePath)) ?? [];
        }
        catch (Exception ex)
        {
            LogError("startup", $"读取旧版 Cookie 缓存失败: {LegacyCookieCacheFilePath}", ex);
            AddStartupWarning("旧版 Cookie 缓存读取失败，已忽略旧缓存");
            return [];
        }
    }

    private void SaveSettings()
    {
        NormalizePlayerWindowSettings();
        AtomicFile.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(_settings, FavoritesJsonOptions));
    }

    private void NormalizePlayerWindowSettings()
    {
        var state = _settings.PlayerWindow;
        state.Width = NormalizeFiniteOrDefault(state.Width, 920);
        state.Height = NormalizeFiniteOrDefault(state.Height, 620);
        state.Left = NormalizeFiniteOrNull(state.Left);
        state.Top = NormalizeFiniteOrNull(state.Top);
        if (!Enum.IsDefined(state.WindowState))
        {
            state.WindowState = WindowState.Normal;
        }

        // 清掉 settings.json 里可能被手工改坏的进度值（空 key / NaN / 负数）。
        foreach (var key in state.PlaybackPositions
                     .Where(pair => string.IsNullOrWhiteSpace(pair.Key) || !double.IsFinite(pair.Value) || pair.Value <= 0)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            state.PlaybackPositions.Remove(key);
        }
    }

    private static double NormalizeFiniteOrDefault(double value, double fallback)
    {
        return double.IsFinite(value) && value > 0 ? value : fallback;
    }

    private static double? NormalizeFiniteOrNull(double? value)
    {
        return value.HasValue && double.IsFinite(value.Value) ? value.Value : null;
    }

    private string CreateOperationId(string prefix)
    {
        return $"{prefix}-{Interlocked.Increment(ref _operationSequence):D6}";
    }

    private void LogInfo(string category, string message)
    {
        AppLogger.Info(category, message);
    }

    private void LogInfoThrottled(string category, string message, TimeSpan interval)
    {
        AppLogger.InfoThrottled(category, message, interval);
    }

    private void LogError(string category, string message, Exception? ex = null)
    {
        AppLogger.Error(category, message, ex);
    }

    private void HandleUiActionError(string category, string fallbackMessage, Exception ex)
    {
        LogError(category, fallbackMessage, ex);
        StatusText.Text = string.IsNullOrWhiteSpace(ex.Message) ? fallbackMessage : $"{fallbackMessage}: {ex.Message}";
    }

    private void AddStartupWarning(string message)
    {
        if (!_startupWarnings.Contains(message, StringComparer.Ordinal))
        {
            _startupWarnings.Add(message);
        }
    }

    private void ApplyStartupWarnings(string fallbackStatus)
    {
        StatusText.Text = _startupWarnings.Count == 0 ? fallbackStatus : string.Join("；", _startupWarnings);
    }

    private Dictionary<string, List<VideoSummary>> ParseFavoriteImport(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, List<VideoSummary>>(StringComparer.OrdinalIgnoreCase)
            {
                [DefaultFavoritesFolder] = []
            };
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => new Dictionary<string, List<VideoSummary>>(StringComparer.OrdinalIgnoreCase)
            {
                [DefaultFavoritesFolder] = ParseFavoriteVideos(document.RootElement)
            },
            JsonValueKind.Object => ParseFavoriteFolders(document.RootElement),
            _ => throw new InvalidOperationException("收藏夹文件格式不支持。")
        };
    }

    private Dictionary<string, List<VideoSummary>> ParseFavoriteFolders(JsonElement root)
    {
        var folders = new Dictionary<string, List<VideoSummary>>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var folderName = string.IsNullOrWhiteSpace(property.Name) ? DefaultFavoritesFolder : property.Name.Trim();
            folders[folderName] = ParseFavoriteVideos(property.Value);
        }

        if (folders.Count == 0)
        {
            folders[DefaultFavoritesFolder] = [];
        }

        return folders;
    }

    private static string ReadJsonString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private string? PromptForFolderName(string title, string prompt, string? defaultValue = null)
    {
        var dialog = new InputDialog(title, prompt, defaultValue) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.InputText.Trim() : null;
    }

    private void ApplyListCoverSettingChange(bool previousShowListCovers)
    {
        if (_settings.ShowListCovers)
        {
            PrimeThumbnails(_searchResults);
            PrimeThumbnails(_favoriteFolders.Values.SelectMany(items => items));
            PrimeThumbnails(GetCurrentRelatedVideos());
            return;
        }

        if (!previousShowListCovers)
        {
            return;
        }
    }

    private IReadOnlyList<VideoSummary> GetCurrentRelatedVideos()
    {
        return RelatedList.ItemsSource as IReadOnlyList<VideoSummary>
            ?? (RelatedList.ItemsSource as IEnumerable<VideoSummary>)?.ToList()
            ?? [];
    }

    private static void RestartFilterTimer(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    private async Task PrimeThumbnailAsync(VideoSummary summary)
    {
        if (!_settings.ShowListCovers || summary.CoverImage is not null || string.IsNullOrWhiteSpace(summary.CoverUrl))
        {
            return;
        }

        var image = await ThumbnailCacheService.GetAsync(summary.CoverUrl, 160);
        if (image is null || !_settings.ShowListCovers)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_settings.ShowListCovers)
                {
                    summary.CoverImage = image;
                }
            });
            return;
        }

        if (_settings.ShowListCovers)
        {
            summary.CoverImage = image;
        }
    }

    private void PrimeThumbnails(IEnumerable<VideoSummary> items)
    {
        if (!_settings.ShowListCovers)
        {
            return;
        }

        // 一次性加载全部封面（用户偏好）。缓存命中/磁盘缓存会挡住重复请求，
        // 并发由 ThumbnailCacheService 的 8 路信号量限制。
        foreach (var item in items)
        {
            _ = PrimeThumbnailAsync(item);
        }
    }

    private VideoSummary? ParseSummaryFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var query = uri.Query.TrimStart('?');
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var segments = pair.Split('=', 2);
                if (segments.Length == 2 && string.Equals(segments[0], "v", StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = Uri.UnescapeDataString(segments[1]);
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        return new VideoSummary
                        {
                            VideoId = candidate,
                            Title = $"视频 {candidate}",
                            Url = $"https://{_settings.SiteHost}/watch?v={candidate}",
                            CoverUrl = string.Empty
                        };
                    }
                }
            }
        }

        var match = BuildVideoLinkRegex().Match(url);
        if (!match.Success)
        {
            return null;
        }

        var videoId = match.Groups[1].Value;
        return new VideoSummary
        {
            VideoId = videoId,
            Title = $"视频 {videoId}",
            Url = $"https://{_settings.SiteHost}/watch?v={videoId}",
            CoverUrl = string.Empty
        };
    }

    private void QueueSourceInline_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDetails || sender is not FrameworkElement { Tag: VideoSource source })
        {
            return;
        }

        QueueVideoSource(source);
    }

    private void DownloadSourceInline_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDetails || sender is not FrameworkElement { Tag: VideoSource source })
        {
            return;
        }

        QueueVideoSource(source, startImmediately: true);
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatBytes(long bytes)
    {
        var value = bytes;
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)value;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return $"{duration.Minutes:00}:{duration.Seconds:00}";
    }
}
