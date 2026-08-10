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
    private static readonly string AppDataDir = AppContext.BaseDirectory;
    private static readonly string FavoritesFilePath = Path.Combine(AppDataDir, "favorites.json");
    private static readonly string DownloadHistoryFilePath = Path.Combine(AppDataDir, "download_history.json");
    private static readonly string DownloadQueueFilePath = Path.Combine(AppDataDir, "download_queue.json");
    private static readonly string LegacyCookieCacheFilePath = Path.Combine(AppDataDir, "cookies.json");
    private static readonly string SettingsFilePath = Path.Combine(AppDataDir, "settings.json");
    private static readonly JsonSerializerOptions FavoritesJsonOptions = new() { WriteIndented = true };

    private readonly AppState _appState = new();
    private readonly AppSettings _settings = new();
    private readonly SearchFilterOptions _searchFilters = new();
    private readonly ObservableCollection<VideoSummary> _searchResults = [];
    private readonly ObservableCollection<DownloadHistoryItem> _historyItems = [];
    private readonly ObservableCollection<DownloadQueueItem> _downloadQueue = [];
    private readonly Dictionary<string, ObservableCollection<VideoSummary>> _favoriteFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VideoDetails> _videoDetailsCache = new(StringComparer.OrdinalIgnoreCase);
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
    private static readonly Regex VideoLinkRegex = new(@"https?://(?:hanime1\.me|hanime1\.com|hanimeone\.me|javchu\.com)/watch\?v=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly List<string> _startupWarnings = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public Visibility ShowListCoversVisibility => _settings.ShowListCovers ? Visibility.Visible : Visibility.Collapsed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
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
        LoadFavorites();
        LoadDownloadHistory();
        if (_settings.PersistDownloadQueue)
        {
            LoadDownloadQueue();
        }
        ResultsList.ItemsSource = _searchResults;
        HistoryList.ItemsSource = _historyItems;
        DownloadQueueList.ItemsSource = _downloadQueue;
        FavoritesFolderBox.ItemsSource = _favoriteFolders.Keys.ToList();
        FavoritesFolderBox.SelectedItem = DefaultFavoritesFolder;
        RefreshFavoritesView();
        RefreshHistoryView();
        PreviewCoverButton.IsEnabled = false;
        QueueSourceButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        ApplyVideoDetailsVisibility();
        ApplyCompactMode();
        UpdatePageNavigationUi();
        UpdateFilterSummaryUi();
        ResetQueueRunSummaryState();
        UpdateDownloadQueueControlUi();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window != this && window.IsLoaded)
                window.Close();
        }

        if (!_settings.PersistDownloadQueue)
        {
            foreach (var item in _downloadQueue)
                DeleteQueueItemTemporaryFile(item);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _cloudflareWindow ??= new CloudflareWindow(_settings.SiteHost) { Owner = this };
            _cloudflareWindow.Closed += (_, _) => _cloudflareWindow = null;
            var cached = LoadCookieCache();
            if (cached.Count > 0)
            {
                try
                {
                    await _cloudflareWindow.ImportCookiesAsync(cached);
                }
                catch
                {
                }
            }

            try
            {
                var reused = await _cloudflareWindow.TryReuseSessionAsync();
                if (reused)
                {
                    await SyncVerifiedSessionAsync();
                    ApplyStartupWarnings($"已自动恢复 Cloudflare 会话。当前共 {_appState.Cookies.Count} 个 Cookie。");
                    return;
                }
            }
            catch
            {
            }

            InitSessionWithoutCf();
            ApplyStartupWarnings("已启动，如遇访问问题请手动验证。");
        }
        catch (Exception ex)
        {
            HandleUiActionError("startup", "初始化失败", ex);
            InitSessionWithoutCf();
        }
    }

    private void InitSessionWithoutCf(IReadOnlyList<BrowserCookieRecord>? cookies = null, string? browserVersion = null)
    {
        _cloudflareWindow ??= new CloudflareWindow(_settings.SiteHost) { Owner = this };
        var effectiveCookies = cookies ?? [];
        _httpClient?.Dispose();
        _httpClient = new HanimeHttpClientFactory().Create(effectiveCookies, _settings.SiteHost, browserVersion);
        _apiClient = new HanimeApiClient(_cloudflareWindow, _httpClient, _settings.SiteHost);
        _downloadService = new DownloadService(_httpClient, _settings.SiteHost);
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
        _httpClient?.Dispose();
        _httpClient = new HanimeHttpClientFactory().Create(_appState.Cookies, _settings.SiteHost, _appState.BrowserVersion);
        _apiClient = new HanimeApiClient(_cloudflareWindow, _httpClient, _settings.SiteHost);
        _downloadService = new DownloadService(_httpClient, _settings.SiteHost);
        _videoDetailsCache.Clear();
        _videoDetailsInFlight.Clear();
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
            .GroupBy(item => item.VideoId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        StatusText.Text = $"正在为 {distinctItems.Count} 个队列项重新获取下载链接...";
        var refreshed = 0;
        foreach (var item in distinctItems)
        {
            try
            {
                var match = await ResolveQueueItemSourceAsync(item);
                if (match is null)
                {
                    continue;
                }

                refreshed++;
            }
            catch
            {
            }
        }

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
               ex.Message.Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase);
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
        StatusText.Text = $"{reason} 请在弹出的验证窗口中完成验证。";
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

    private async void SearchButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isSearching)
        {
            return;
        }

        try
        {
            var keyword = GetSearchKeyword();
            var directVideoMatch = VideoLinkRegex.Match(keyword);
            var isNumericId = !directVideoMatch.Success && System.Text.RegularExpressions.Regex.IsMatch(keyword.Trim(), @"^\d+$");
            if (directVideoMatch.Success || isNumericId)
            {
                var videoId = directVideoMatch.Success ? directVideoMatch.Groups[1].Value : keyword.Trim();
                var summary = new VideoSummary
                {
                    VideoId = videoId,
                    Title = keyword,
                    Url = $"https://{_settings.SiteHost}/watch?v={videoId}",
                    CoverUrl = string.Empty
                };
                ResultsList.SelectedItem = null;
                FavoritesList.SelectedItem = null;
                await LoadDetailsAsync(summary);
                return;
            }

            _currentSearchKeyword = keyword;
            await SearchAsync(1);
        }
        catch (Exception ex)
        {
            HandleUiActionError("search", "搜索失败", ex);
        }
    }

    private void SearchBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        SearchButton_OnClick(sender, new RoutedEventArgs());
    }

    private string GetSearchKeyword()
    {
        return SearchBox.Text?.Trim() ?? string.Empty;
    }


    private CancellationTokenSource? _titleCopiedHintCts;

    private async void TitleText_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var title = TitleText.Text;
        if (string.IsNullOrWhiteSpace(title) || title == "请选择左侧视频查看详情。") return;
        try { Clipboard.SetDataObject(title, false); } catch { return; }
        var pos = e.GetPosition(TitleText);
        Canvas.SetLeft(TitleCopiedHint, pos.X);
        TitleCopiedHint.Visibility = Visibility.Visible;
        _titleCopiedHintCts?.Cancel();
        _titleCopiedHintCts = new CancellationTokenSource();
        var cts = _titleCopiedHintCts;
        try { await Task.Delay(1500, cts.Token); } catch (OperationCanceledException) { return; }
        TitleCopiedHint.Visibility = Visibility.Collapsed;
    }

    private void DownloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DownloadButton.IsEnabled is false)
        {
            return;
        }

        if (SourcesList.SelectedItem is not VideoSource source)
        {
            StatusText.Text = "请先选择一个视频源。";
            return;
        }

        QueueVideoSource(source, startImmediately: true);
    }

    private async void FilterButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var previousFilters = CloneSearchFilters(_searchFilters);
            var dialog = new FilterDialog(_searchFilters) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            _searchFilters.Genre = dialog.FilterOptions.Genre;
            _searchFilters.Sort = dialog.FilterOptions.Sort;
            _searchFilters.Date = dialog.FilterOptions.Date;
            _searchFilters.Duration = dialog.FilterOptions.Duration;
            _searchFilters.Tags = dialog.FilterOptions.Tags.ToList();
            _searchFilters.Broad = dialog.FilterOptions.Broad;
            UpdateFilterSummaryUi();
            await RefreshSearchAfterFilterChangeAsync(previousFilters);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开筛选失败: {ex.Message}";
        }
    }

    private async void ClearFiltersButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var previousFilters = CloneSearchFilters(_searchFilters);
            _searchFilters.Genre = string.Empty;
            _searchFilters.Sort = string.Empty;
            _searchFilters.Date = string.Empty;
            _searchFilters.Duration = string.Empty;
            _searchFilters.Tags = [];
            _searchFilters.Broad = false;
            UpdateFilterSummaryUi();
            await RefreshSearchAfterFilterChangeAsync(previousFilters);
        }
        catch (Exception ex)
        {
            HandleUiActionError("search", "清空筛选失败", ex);
        }
    }

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
            _settings.CompactMode = dialog.Settings.CompactMode;
            _settings.ThemeMode = AppThemeService.Normalize(dialog.Settings.ThemeMode);
            _settings.DefaultQuality = dialog.Settings.DefaultQuality;
            _settings.SiteHost = dialog.Settings.SiteHost;
            _settings.CustomSiteHosts = dialog.Settings.CustomSiteHosts.ToList();
            _settings.PersistDownloadQueue = dialog.Settings.PersistDownloadQueue;
            _settings.MaxConcurrentDownloads = Math.Clamp(dialog.Settings.MaxConcurrentDownloads, 1, 3);
            _settings.VideoDetailsVisibility = dialog.Settings.VideoDetailsVisibility;
            AppThemeService.Apply(Application.Current, _settings.ThemeMode);
            SaveSettings();
            _videoDetailsCache.Clear();
            _videoDetailsInFlight.Clear();
            RefreshFavoritesView();
            ApplyVideoDetailsVisibility();
            ApplyCompactMode();
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

    private async void FirstPageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentPage <= 1)
        {
            return;
        }

        try
        {
            await SearchAsync(1);
        }
        catch (Exception ex)
        {
            HandleUiActionError("search", "翻页失败", ex);
        }
    }

    private async void PreviousPageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentPage <= 1)
        {
            return;
        }

        try
        {
            await SearchAsync(_currentPage - 1);
        }
        catch (Exception ex)
        {
            HandleUiActionError("search", "翻页失败", ex);
        }
    }

    private async void PageNumberButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not int page || page == _currentPage)
        {
            return;
        }

        try
        {
            await SearchAsync(page);
        }
        catch (Exception ex)
        {
            HandleUiActionError("search", "翻页失败", ex);
        }
    }

    private async void NextPageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentPage >= _totalPages)
        {
            return;
        }

        try
        {
            await SearchAsync(_currentPage + 1);
        }
        catch (Exception ex)
        {
            HandleUiActionError("search", "翻页失败", ex);
        }
    }

    private async void LastPageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentPage >= _totalPages)
        {
            return;
        }

        try
        {
            await SearchAsync(_totalPages);
        }
        catch (Exception ex)
        {
            HandleUiActionError("search", "翻页失败", ex);
        }
    }

    private void AddFavoriteButton_OnClick(object sender, RoutedEventArgs e)
    {
        var summary = GetSelectedVideoSummary();
        if (summary is null)
        {
            StatusText.Text = "请先在搜索结果或收藏夹里选中一个视频。";
            return;
        }

        if (IsVideoFavorited(summary.VideoId))
        {
            RemoveVideoFromFavorites(summary.VideoId);
            return;
        }

        if (_favoriteFolders.Count > 1)
        {
            var button = sender as Button ?? AddFavoriteButton;
            var menu = new ContextMenu();
            foreach (var folderName in _favoriteFolders.Keys)
            {
                var name = folderName;
                var item = new MenuItem { Header = name };
                item.Click += (_, _) => AddVideoToFavoriteFolder(summary, name);
                menu.Items.Add(item);
            }
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
            return;
        }

        var folder = FavoritesFolderBox.SelectedItem as string ?? DefaultFavoritesFolder;
        AddVideoToFavoriteFolder(summary, folder);
    }

    private bool IsVideoFavorited(string videoId)
    {
        return _favoriteFolders.Values.Any(items => items.Any(item => string.Equals(item.VideoId, videoId, StringComparison.OrdinalIgnoreCase)));
    }

    private void RemoveVideoFromFavorites(string videoId)
    {
        var removedCount = 0;
        foreach (var favorites in _favoriteFolders.Values)
        {
            var removedItems = favorites.Where(item => string.Equals(item.VideoId, videoId, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var item in removedItems)
            {
                favorites.Remove(item);
            }
            removedCount += removedItems.Count;
        }

        RefreshFavoritesView();
        UpdateFavoriteButtonState(videoId);

        if (removedCount == 0)
        {
            StatusText.Text = "当前视频不在收藏夹中。";
            return;
        }

        if (!TrySaveFavorites("favorites", "保存收藏夹失败"))
        {
            StatusText.Text = "已取消收藏，但收藏夹保存失败。";
            return;
        }

        StatusText.Text = "已取消收藏。";
    }

    private void UpdateFavoriteButtonState(string? videoId = null)
    {
        var currentVideoId = videoId ?? _currentDetails?.VideoId ?? GetSelectedVideoSummary()?.VideoId;
        AddFavoriteButton.Content = !string.IsNullOrWhiteSpace(currentVideoId) && IsVideoFavorited(currentVideoId) ? "取消收藏" : "收藏";
    }

    private void AddVideoToFavoriteFolder(VideoSummary summary, string folderName)
    {
        if (!_favoriteFolders.TryGetValue(folderName, out var favorites))
        {
            favorites = [];
            _favoriteFolders[folderName] = favorites;
            RefreshFavoriteFolders();
        }

        if (favorites.Any(item => item.VideoId == summary.VideoId))
        {
            StatusText.Text = $"{summary.VideoId} 已在 {folderName} 中。";
            return;
        }

        favorites.Add(summary);
        SaveFavorites();
        RefreshFavoritesView();
        UpdateFavoriteButtonState(summary.VideoId);
        StatusText.Text = $"已加入收藏夹: {summary.Title}";
    }

    private void ListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        var item = GetListBoxItemAt(listBox, e.GetPosition(listBox));
        if (item is null) return;

        if (ReferenceEquals(listBox, DownloadQueueList))
        {
            if (item.DataContext is DownloadQueueItem queueItem)
            {
                var isCtrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                if (isCtrlPressed)
                {
                    _queueCtrlSelectMode = true;
                    _queueCtrlClickedItem = queueItem;
                    _queueDragItems = null;
                    _dragSelectList = listBox;
                    _dragSelectStartIndex = listBox.Items.IndexOf(item.DataContext);
                    _dragSelectStartPoint = e.GetPosition(listBox);
                    _dragSelectInitialItems = listBox.SelectedItems.Cast<object>().ToList();
                    _dragSelectExtendSelection = true;
                    _isDragSelecting = false;
                    e.Handled = true;
                    return;
                }

                _queueCtrlSelectMode = false;
                _queueCtrlClickedItem = null;
                _dragSelectExtendSelection = false;
                _dragSelectInitialItems.Clear();
                if (!DownloadQueueList.SelectedItems.Contains(queueItem))
                {
                    DownloadQueueList.SelectedItem = queueItem;
                }

                _queueDragItems = DownloadQueueList.SelectedItems.Cast<DownloadQueueItem>().Where(_downloadQueue.Contains).Distinct().ToList();
                _queueDragStartPoint = e.GetPosition(DownloadQueueList);
            }
            return;
        }

        _dragSelectList = listBox;
        _dragSelectStartIndex = listBox.Items.IndexOf(item.DataContext);
        _dragSelectStartPoint = e.GetPosition(listBox);
        _dragSelectInitialItems.Clear();
        _dragSelectExtendSelection = false;
        _isDragSelecting = false;
    }

    private void ListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is ListBox queueList && ReferenceEquals(queueList, DownloadQueueList) && !_queueCtrlSelectMode)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _isDownloadingQueue || _queueDragItems is null || _queueDragItems.Count == 0)
            {
                return;
            }

            var dragPosition = e.GetPosition(queueList);
            if (Math.Abs(dragPosition.X - _queueDragStartPoint.X) < 5 && Math.Abs(dragPosition.Y - _queueDragStartPoint.Y) < 5)
            {
                return;
            }

            DragDrop.DoDragDrop(queueList, new DataObject(typeof(List<DownloadQueueItem>), _queueDragItems), DragDropEffects.Move);
            _queueDragItems = null;
            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed || _dragSelectList is null || _dragSelectStartIndex < 0) return;
        if (sender is not ListBox listBox || !ReferenceEquals(listBox, _dragSelectList)) return;
        var pos = e.GetPosition(listBox);
        if (!_isDragSelecting)
        {
            if (Math.Abs(pos.X - _dragSelectStartPoint.X) < 5 && Math.Abs(pos.Y - _dragSelectStartPoint.Y) < 5) return;
            _isDragSelecting = true;
        }

        var item = GetListBoxItemAt(listBox, pos);
        if (item is null) return;
        var endIndex = listBox.Items.IndexOf(item.DataContext);
        if (endIndex < 0) return;

        var start = Math.Min(_dragSelectStartIndex, endIndex);
        var end = Math.Max(_dragSelectStartIndex, endIndex);

        listBox.SelectedItems.Clear();
        if (_dragSelectExtendSelection)
        {
            foreach (var selectedItem in _dragSelectInitialItems.Where(listBox.Items.Contains))
            {
                listBox.SelectedItems.Add(selectedItem);
            }
        }

        for (var i = start; i <= end; i++)
        {
            var currentItem = listBox.Items[i];
            if (!listBox.SelectedItems.Contains(currentItem))
            {
                listBox.SelectedItems.Add(currentItem);
            }
        }
        e.Handled = true;
    }

    private void ListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox && ReferenceEquals(listBox, DownloadQueueList) && _queueCtrlSelectMode && !_isDragSelecting && _queueCtrlClickedItem is not null)
        {
            if (listBox.SelectedItems.Contains(_queueCtrlClickedItem))
            {
                listBox.SelectedItems.Remove(_queueCtrlClickedItem);
            }
            else
            {
                listBox.SelectedItems.Add(_queueCtrlClickedItem);
            }
            e.Handled = true;
        }

        _isDragSelecting = false;
        _dragSelectList = null;
        _dragSelectStartIndex = -1;
        _dragSelectExtendSelection = false;
        _dragSelectInitialItems.Clear();
        _queueCtrlSelectMode = false;
        _queueCtrlClickedItem = null;
        _queueDragItems = null;
    }

    private static ListBoxItem? GetListBoxItemAt(ListBox listBox, Point position)
    {
        var element = listBox.InputHitTest(position) as DependencyObject;
        while (element is not null and not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);
        return element as ListBoxItem;
    }

    private void DownloadQueueList_OnDragOver(object sender, DragEventArgs e)
    {
        if (_isDownloadingQueue || !e.Data.GetDataPresent(typeof(List<DownloadQueueItem>)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private async void DownloadQueueList_OnDrop(object sender, DragEventArgs e)
    {
        if (_isDownloadingQueue || !e.Data.GetDataPresent(typeof(List<DownloadQueueItem>)))
        {
            return;
        }

        try
        {
            if (e.Data.GetData(typeof(List<DownloadQueueItem>)) is not List<DownloadQueueItem> draggedItems || draggedItems.Count == 0)
            {
                return;
            }

            var movingItems = draggedItems.Where(_downloadQueue.Contains).Distinct().ToList();
            if (movingItems.Count == 0)
            {
                return;
            }

            var targetItem = GetListBoxItemAt(DownloadQueueList, e.GetPosition(DownloadQueueList))?.DataContext as DownloadQueueItem;
            var targetIndex = targetItem is null ? _downloadQueue.Count : _downloadQueue.IndexOf(targetItem);
            if (targetIndex < 0)
            {
                targetIndex = _downloadQueue.Count;
            }

            var movingIndexes = movingItems.Select(item => _downloadQueue.IndexOf(item)).Where(index => index >= 0).OrderBy(index => index).ToList();
            if (movingIndexes.Count == 0)
            {
                return;
            }

            var removedBeforeTarget = movingIndexes.Count(index => index < targetIndex);
            var insertIndex = Math.Max(0, targetIndex - removedBeforeTarget);
            var remainingItems = _downloadQueue.Except(movingItems).ToList();
            insertIndex = Math.Min(insertIndex, remainingItems.Count);

            var reordered = remainingItems.Take(insertIndex)
                .Concat(movingItems)
                .Concat(remainingItems.Skip(insertIndex))
                .ToList();

            if (reordered.SequenceEqual(_downloadQueue))
            {
                return;
            }

            _downloadQueue.Clear();
            foreach (var item in reordered)
            {
                _downloadQueue.Add(item);
            }

            if (!await TrySaveDownloadQueueAsync("queue", "保存下载队列失败"))
            {
                StatusText.Text = "已拖动调整队列顺序，但队列保存失败。";
                return;
            }

            DownloadQueueList.SelectedItems.Clear();
            foreach (var item in movingItems)
            {
                DownloadQueueList.SelectedItems.Add(item);
            }
            DownloadQueueList.ScrollIntoView(movingItems.First());
            StatusText.Text = "已调整下载队列顺序。";
        }
        catch (Exception ex)
        {
            HandleUiActionError("queue", "拖动调整队列顺序失败", ex);
        }
    }

    private async void ResultsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_settings.CompactMode) return;
        try
        {
            var items = ResultsList.SelectedItems.OfType<VideoSummary>().ToList();
            if (items.Count == 0) return;
            var added = 0;
            foreach (var summary in items)
            {
                if (HasQueueItem(summary.VideoId, 0)) continue;
                _downloadQueue.Add(new DownloadQueueItem
                {
                    Title = string.IsNullOrWhiteSpace(summary.Title) ? summary.VideoId : summary.Title,
                    Url = summary.Url,
                    Type = "mp4",
                    Quality = 0,
                    VideoId = summary.VideoId,
                    TargetPath = CreateQueueTargetPath(string.IsNullOrWhiteSpace(summary.Title) ? summary.VideoId : summary.Title, "mp4", summary.VideoId, 0),
                    StageText = "等待",
                    QueueStatusText = "等待中"
                });
                added++;
            }
            if (!await TrySaveDownloadQueueAsync("queue", "保存下载队列失败"))
            {
                StatusText.Text = $"已加入下载队列 {added} 项，但队列保存失败。";
                UpdateDownloadQueueControlUi();
                return;
            }

            UpdateDownloadQueueControlUi();
            StatusText.Text = $"已加入下载队列 {added} 项。";
        }
        catch (Exception ex)
        {
            HandleUiActionError("queue", "加入下载队列失败", ex);
        }
    }

    private async void ResultsList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isDragSelecting) return;
        try
        {
            if (ResultsList.SelectedItem is VideoSummary summary)
            {
                FavoritesList.SelectedItem = null;
                HistoryList.SelectedItem = null;
                if (_settings.CompactMode)
                {
                    return;
                }
                await LoadDetailsAsync(summary);
            }
        }
        catch (Exception ex)
        {
            HandleUiActionError("details", "读取详情失败", ex);
        }
    }

    private async void FavoritesList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isDragSelecting) return;
        try
        {
            if (FavoritesList.SelectedItem is VideoSummary summary)
            {
                ResultsList.SelectedItem = null;
                HistoryList.SelectedItem = null;
                if (_settings.CompactMode)
                {
                    return;
                }
                await LoadDetailsAsync(summary);
            }
        }
        catch (Exception ex)
        {
            HandleUiActionError("details", "读取详情失败", ex);
        }
    }

    private void FavoritesFolderBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RefreshFavoritesView();
    }

    private void FavoritesSearchBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RestartFilterTimer(_favoritesFilterTimer);
    }

    private void ResultsList_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowVideoListContextMenu(ResultsList, e.GetPosition(ResultsList));
    }

    private void FavoritesList_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowVideoListContextMenu(FavoritesList, e.GetPosition(FavoritesList), isFavoriteList: true);
    }

    private void RelatedList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isDragSelecting)
        {
            return;
        }

        ResultsList.SelectedItem = null;
        FavoritesList.SelectedItem = null;
        HistoryList.SelectedItem = null;
    }

    private void RelatedList_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = GetListBoxItemAt(RelatedList, e.GetPosition(RelatedList));
        if (item?.DataContext is VideoSummary video && !RelatedList.SelectedItems.Contains(video))
        {
            RelatedList.SelectedItem = video;
        }
        ShowVideoListContextMenu(RelatedList, e.GetPosition(RelatedList));
    }

    private async void RelatedList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (RelatedList.SelectedItem is not VideoSummary summary)
            {
                return;
            }

            if (_settings.CompactMode)
            {
                await ShowVideoDetailsDialogAsync(summary);
                return;
            }

            await LoadDetailsAsync(summary);
        }
        catch (Exception ex)
        {
            HandleUiActionError("details", "读取详情失败", ex);
        }
    }

    private void ShowVideoListContextMenu(ListBox listBox, Point position, bool isFavoriteList = false)
    {
        var selectedVideos = listBox.SelectedItems.Cast<VideoSummary>().ToList();
        if (selectedVideos.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu();
        if (selectedVideos.Count == 1)
        {
            var viewItem = new MenuItem { Header = "查看信息" };
            viewItem.Click += async (_, _) =>
            {
                try
                {
                    if (_settings.CompactMode)
                    {
                        await ShowVideoDetailsDialogAsync(selectedVideos[0]);
                        return;
                    }

                    await LoadDetailsAsync(selectedVideos[0]);
                }
                catch (Exception ex)
                {
                    HandleUiActionError("details", "读取详情失败", ex);
                }
            };
            menu.Items.Add(viewItem);

            if (!isFavoriteList)
            {
                var playItem = new MenuItem { Header = "播放" };
                playItem.Click += async (_, _) => await PlayVideoSummaryAsync(selectedVideos[0]);
                menu.Items.Add(playItem);
            }
        }

        var downloadItem = new MenuItem { Header = isFavoriteList ? "下载" : "加入下载队列" };
        downloadItem.Click += async (_, _) =>
        {
            try
            {
                await QueueVideosForDownloadAsync(selectedVideos);
            }
            catch (Exception ex)
            {
                HandleUiActionError("queue", "加入下载队列失败", ex);
            }
        };
        menu.Items.Add(downloadItem);

        if (isFavoriteList)
        {
            var removeItem = new MenuItem { Header = "移除" };
            removeItem.Click += (_, _) => RemoveSelectedFavorites(selectedVideos);
            menu.Items.Add(removeItem);
        }
        else
        {
            var favoriteItem = new MenuItem { Header = "添加到收藏夹" };
            if (_favoriteFolders.Count > 1)
            {
                foreach (var folderName in _favoriteFolders.Keys)
                {
                    var name = folderName;
                    var sub = new MenuItem { Header = name };
                    sub.Click += (_, _) => AddVideosToFavoriteFolder(selectedVideos, name);
                    favoriteItem.Items.Add(sub);
                }
            }
            else
            {
                favoriteItem.Click += (_, _) => AddVideosToFavorites(selectedVideos);
            }
            menu.Items.Add(favoriteItem);
        }

        menu.PlacementTarget = listBox;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void NewFavoriteFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var folderName = PromptForFolderName("新建收藏夹", "请输入收藏夹名称：");
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        if (_favoriteFolders.ContainsKey(folderName))
        {
            StatusText.Text = $"收藏夹 {folderName} 已存在。";
            return;
        }

        _favoriteFolders[folderName] = [];
        SaveFavorites();
        RefreshFavoriteFolders(folderName);
        RefreshFavoritesView();
        StatusText.Text = $"已新建收藏夹: {folderName}";
    }

    private void DeleteFavoriteFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var currentFolder = FavoritesFolderBox.SelectedItem as string ?? DefaultFavoritesFolder;
        if (string.Equals(currentFolder, DefaultFavoritesFolder, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "默认收藏夹不能删除。";
            return;
        }

        if (MessageBox.Show(this, $"确定删除收藏夹 '{currentFolder}' 吗？", "删除收藏夹", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_favoriteFolders.TryGetValue(currentFolder, out var folderItems) || !_favoriteFolders.Remove(currentFolder))
        {
            StatusText.Text = $"未找到收藏夹: {currentFolder}";
            return;
        }

        if (!TrySaveFavorites("favorites", "保存收藏夹失败"))
        {
            RefreshFavoriteFolders(DefaultFavoritesFolder);
            RefreshFavoritesView();
            StatusText.Text = $"已删除收藏夹: {currentFolder}，但收藏夹保存失败。";
            return;
        }

        RefreshFavoriteFolders(DefaultFavoritesFolder);
        RefreshFavoritesView();
        StatusText.Text = $"已删除收藏夹: {currentFolder}";
    }

    private void RenameFavoriteFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var currentFolder = FavoritesFolderBox.SelectedItem as string ?? DefaultFavoritesFolder;
        if (string.Equals(currentFolder, DefaultFavoritesFolder, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "默认收藏夹不能重命名。";
            return;
        }

        var folderName = PromptForFolderName("重命名收藏夹", "请输入新的收藏夹名称：", currentFolder);
        if (string.IsNullOrWhiteSpace(folderName) || string.Equals(folderName, currentFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_favoriteFolders.ContainsKey(folderName))
        {
            StatusText.Text = $"收藏夹 {folderName} 已存在。";
            return;
        }

        if (!_favoriteFolders.Remove(currentFolder, out var favorites))
        {
            StatusText.Text = $"未找到收藏夹: {currentFolder}";
            return;
        }

        _favoriteFolders[folderName] = favorites;
        if (!TrySaveFavorites("favorites", "保存收藏夹失败"))
        {
            RefreshFavoriteFolders(folderName);
            RefreshFavoritesView();
            StatusText.Text = $"已将 {currentFolder} 重命名为 {folderName}，但收藏夹保存失败。";
            return;
        }

        RefreshFavoriteFolders(folderName);
        RefreshFavoritesView();
        StatusText.Text = $"已将 {currentFolder} 重命名为 {folderName}。";
    }

    private void ExportFavoritesButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            ExportFavorites(currentFolderOnly: true);
            return;
        }

        var menu = new ContextMenu();
        var exportCurrentItem = new MenuItem { Header = "导出当前收藏夹" };
        exportCurrentItem.Click += (_, _) => ExportFavorites(currentFolderOnly: true);
        menu.Items.Add(exportCurrentItem);

        var exportAllItem = new MenuItem { Header = "导出全部收藏夹" };
        exportAllItem.Click += (_, _) => ExportFavorites(currentFolderOnly: false);
        menu.Items.Add(exportAllItem);

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ExportFavorites(bool currentFolderOnly)
    {
        try
        {
            var currentFolder = FavoritesFolderBox.SelectedItem as string ?? DefaultFavoritesFolder;
            var dialog = new SaveFileDialog
            {
                FileName = currentFolderOnly ? $"{SanitizeFileName(currentFolder)}.json" : "favorites.json",
                Filter = "JSON Files|*.json|All Files|*.*"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var exportData = currentFolderOnly
                ? new Dictionary<string, List<FavoriteVideoRecord>>(StringComparer.OrdinalIgnoreCase)
                {
                    [currentFolder] = GetFavoriteRecords(_favoriteFolders[currentFolder])
                }
                : _favoriteFolders.ToDictionary(
                    item => item.Key,
                    item => GetFavoriteRecords(item.Value),
                    StringComparer.OrdinalIgnoreCase);

            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(exportData, FavoritesJsonOptions));
            StatusText.Text = currentFolderOnly ? $"已导出收藏夹: {currentFolder}" : $"已导出全部收藏夹，共 {exportData.Count} 个文件夹。";
        }
        catch (Exception ex)
        {
            HandleUiActionError("favorites", "导出收藏夹失败", ex);
        }
    }

    private void ImportFavoritesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON Files|*.json|All Files|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var importedFolders = ParseFavoriteImport(File.ReadAllText(dialog.FileName));
            var importedCount = 0;
            var addedVideos = new List<VideoSummary>();
            foreach (var (folderName, videos) in importedFolders)
            {
                var targetFolderName = ResolveImportFolderName(folderName);
                if (targetFolderName is null)
                {
                    StatusText.Text = "已取消导入。";
                    return;
                }

                if (!_favoriteFolders.TryGetValue(targetFolderName, out var existingFolder))
                {
                    existingFolder = [];
                    _favoriteFolders[targetFolderName] = existingFolder;
                }

                var existingIds = existingFolder.Select(item => item.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var video in videos.Where(video => existingIds.Add(video.VideoId)))
                {
                    existingFolder.Add(video);
                    addedVideos.Add(video);
                    importedCount++;
                }
            }

            if (!TrySaveFavorites("favorites", "保存收藏夹失败"))
            {
                RefreshFavoriteFolders();
                RefreshFavoritesView();
                StatusText.Text = $"导入完成，共新增 {importedCount} 条收藏，但收藏夹保存失败。";
                return;
            }

            RefreshFavoriteFolders();
            RefreshFavoritesView();
            StatusText.Text = $"导入完成，共新增 {importedCount} 条收藏。";
            FetchMissingCovers(addedVideos);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"导入收藏夹失败: {ex.Message}";
        }
    }

    private void RefreshHistoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        LoadDownloadHistory();
        RefreshHistoryView();
        StatusText.Text = $"下载历史共 {_historyItems.Count} 条。";
    }

    private async void ClearHistoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MessageBox.Show(this, "确定清空下载历史吗？", "清空下载历史", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            _historyItems.Clear();
            if (!await TrySaveDownloadHistoryAsync("history", "保存下载历史失败"))
            {
                RefreshHistoryView();
                StatusText.Text = "已清空下载历史，但历史保存失败。";
                return;
            }

            RefreshHistoryView();
            StatusText.Text = "已清空下载历史。";
        }
        catch (Exception ex)
        {
            HandleUiActionError("history", "清空下载历史失败", ex);
        }
    }

    private async void HistoryList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isDragSelecting) return;
        try
        {
            if (HistoryList.SelectedItem is not DownloadHistoryItem historyItem)
            {
                return;
            }

            await LoadHistoryItemDetailsAsync(historyItem);
        }
        catch (Exception ex)
        {
            HandleUiActionError("history", "读取历史详情失败", ex);
        }
    }

    private void HistoryList_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = GetListBoxItemAt(HistoryList, e.GetPosition(HistoryList));
        if (item?.DataContext is DownloadHistoryItem historyItem && !HistoryList.SelectedItems.Contains(historyItem))
        {
            HistoryList.SelectedItem = historyItem;
        }
    }

    private void HistoryList_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var selectedHistoryItems = HistoryList.SelectedItems.Cast<DownloadHistoryItem>().ToList();
        if (selectedHistoryItems.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu();
        var viewItem = new MenuItem { Header = "查看信息" };
        viewItem.Click += async (_, _) =>
        {
            try
            {
                await LoadHistoryItemDetailsAsync(selectedHistoryItems[0]);
            }
            catch (Exception ex)
            {
                HandleUiActionError("history", "读取历史详情失败", ex);
            }
        };
        menu.Items.Add(viewItem);

        var openFolderItem = new MenuItem { Header = "打开所在目录" };
        openFolderItem.Click += (_, _) => OpenHistoryItemFolder(selectedHistoryItems[0]);
        menu.Items.Add(openFolderItem);

        menu.PlacementTarget = HistoryList;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void QueueSourceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not VideoSource source)
        {
            StatusText.Text = "请先选择一个视频源。";
            return;
        }

        QueueVideoSource(source);
    }

    private async void QueueVideoSource(VideoSource source, bool startImmediately = false)
    {
        try
        {
            var title = _currentDetails?.Title ?? GetSelectedVideoSummary()?.Title ?? "未命名视频";
            var videoId = _currentDetailsVideoId ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(videoId) && HasQueueItem(videoId, source.Quality))
            {
                StatusText.Text = "该视频源已经在下载队列中。";
                return;
            }

            var queueItem = new DownloadQueueItem
            {
                Title = title,
                Url = source.Url,
                Type = source.Type,
                Quality = source.Quality,
                VideoId = videoId,
                TargetPath = CreateQueueTargetPath(title, source.Type, videoId, source.Quality),
                StageText = "等待",
                QueueStatusText = "等待中"
            };

            if (_isDownloadingQueue)
            {
                var activeIndexes = _downloadQueue
                    .Select((item, index) => new { item, index })
                    .Where(entry => entry.item.IsDownloading)
                    .Select(entry => entry.index)
                    .ToList();
                var insertIndex = activeIndexes.Count > 0 ? Math.Min(activeIndexes.Max() + 1, _downloadQueue.Count) : _downloadQueue.Count;
                _downloadQueue.Insert(insertIndex, queueItem);
            }
            else
            {
                _downloadQueue.Add(queueItem);
            }

            if (!await TrySaveDownloadQueueAsync("queue", "保存下载队列失败"))
            {
                NotifyDownloadQueueChanged();
                UpdateDownloadQueueControlUi();
                StatusText.Text = _isDownloadingQueue ? "已加入下载队列，但队列保存失败。" : "已加入下载队列，但队列保存失败。";
                return;
            }

            NotifyDownloadQueueChanged();
            UpdateDownloadQueueControlUi();
            StatusText.Text = _isDownloadingQueue
                ? "已加入下载队列。"
                : (startImmediately ? "已加入下载队列并开始下载。" : "已加入下载队列。");

            if (startImmediately && !_isDownloadingQueue)
            {
                await StartQueueDownloadAsync();
            }
        }
        catch (Exception ex)
        {
            HandleUiActionError("queue", "加入下载队列失败", ex);
        }
    }

    private async void DownloadQueueButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isDownloadingQueue)
            {
                _isPauseRequested = true;
                _downloadQueueCancellationTokenSource?.Cancel();
                StatusText.Text = "正在暂停下载队列...";
                return;
            }

            if (_downloadQueue.Count == 0)
            {
                StatusText.Text = "下载队列为空。";
                return;
            }

            if (_hasPausedQueue)
            {
                await StartQueueDownloadAsync();
                return;
            }

            await StartQueueDownloadAsync();
        }
        catch (Exception ex)
        {
            HandleUiActionError("queue", "启动下载队列失败", ex);
        }
    }

    private void DownloadQueueList_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = GetListBoxItemAt(DownloadQueueList, e.GetPosition(DownloadQueueList));
        if (item?.DataContext is DownloadQueueItem queueItem && !DownloadQueueList.SelectedItems.Contains(queueItem))
        {
            DownloadQueueList.SelectedItem = queueItem;
        }
    }

    private void DownloadQueueList_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var selectedQueueItems = DownloadQueueList.SelectedItems.Cast<DownloadQueueItem>().ToList();
        if (selectedQueueItems.Count == 0)
        {
            return;
        }

        var selectedIndexes = selectedQueueItems
            .Select(item => _downloadQueue.IndexOf(item))
            .Where(index => index >= 0)
            .OrderBy(index => index)
            .ToList();
        var canMoveUp = selectedIndexes.Count > 0 && selectedIndexes.First() > 0;
        var canMoveDown = selectedIndexes.Count > 0 && selectedIndexes.Last() < _downloadQueue.Count - 1;

        var menu = new ContextMenu();
        var startItem = new MenuItem { Header = _isDownloadingQueue ? "暂停下载队列" : (_hasPausedQueue ? "继续选中项" : "开始选中项") };
        startItem.Click += async (_, _) =>
        {
            try
            {
                if (_isDownloadingQueue)
                {
                    _isPauseRequested = true;
                    _downloadQueueCancellationTokenSource?.Cancel();
                    StatusText.Text = "正在暂停下载队列...";
                    return;
                }

                await StartSelectedQueueItemsAsync(selectedQueueItems);
            }
            catch (Exception ex)
            {
                HandleUiActionError("queue", "启动选中下载项失败", ex);
            }
        };
        menu.Items.Add(startItem);

        if (selectedQueueItems.Any(item => item.QueueState == DownloadQueueState.Error))
        {
            var reResolveItem = new MenuItem { Header = "重新解析选中失败项" };
            reResolveItem.Click += async (_, _) =>
            {
                try
                {
                    var items = selectedQueueItems.Where(item => item.QueueState == DownloadQueueState.Error).ToList();
                    await ReResolveQueueItemsAsync(items);
                }
                catch (Exception ex)
                {
                    HandleUiActionError("queue", "重新解析失败", ex);
                }
            };
            menu.Items.Add(reResolveItem);
        }

        if (!_isDownloadingQueue)
        {
            var moveUpItem = new MenuItem { Header = "上移", IsEnabled = canMoveUp };
            moveUpItem.Click += async (_, _) =>
            {
                try
                {
                    await MoveQueueItemsAsync(selectedQueueItems, indexes => indexes.Select(index => index - 1).ToList(), "已上移选中项。");
                }
                catch (Exception ex)
                {
                    HandleUiActionError("queue", "上移队列项失败", ex);
                }
            };
            menu.Items.Add(moveUpItem);

            var moveDownItem = new MenuItem { Header = "下移", IsEnabled = canMoveDown };
            moveDownItem.Click += async (_, _) =>
            {
                try
                {
                    await MoveQueueItemsAsync(selectedQueueItems, indexes => indexes.Select(index => index + 1).ToList(), "已下移选中项。");
                }
                catch (Exception ex)
                {
                    HandleUiActionError("queue", "下移队列项失败", ex);
                }
            };
            menu.Items.Add(moveDownItem);

            var moveTopItem = new MenuItem { Header = "置顶", IsEnabled = canMoveUp };
            moveTopItem.Click += async (_, _) =>
            {
                try
                {
                    await MoveQueueItemsAsync(selectedQueueItems, indexes => Enumerable.Range(0, indexes.Count).ToList(), "已将选中项置顶。");
                }
                catch (Exception ex)
                {
                    HandleUiActionError("queue", "置顶队列项失败", ex);
                }
            };
            menu.Items.Add(moveTopItem);

            var moveBottomItem = new MenuItem { Header = "置底", IsEnabled = canMoveDown };
            moveBottomItem.Click += async (_, _) =>
            {
                try
                {
                    await MoveQueueItemsAsync(selectedQueueItems, indexes => Enumerable.Range(_downloadQueue.Count - indexes.Count, indexes.Count).ToList(), "已将选中项置底。");
                }
                catch (Exception ex)
                {
                    HandleUiActionError("queue", "置底队列项失败", ex);
                }
            };
            menu.Items.Add(moveBottomItem);
        }

        var removeItem = new MenuItem { Header = "移除选中项" };
        removeItem.Click += (_, _) => RemoveSelectedQueueItems(selectedQueueItems);
        menu.Items.Add(removeItem);

        menu.PlacementTarget = DownloadQueueList;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private async void ReResolveFailedQueueItemsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var items = _downloadQueue.Where(item => item.QueueState == DownloadQueueState.Error).ToList();
            if (items.Count == 0)
            {
                StatusText.Text = "当前没有失败项可重新解析。";
                return;
            }

            await ReResolveQueueItemsAsync(items);
        }
        catch (Exception ex)
        {
            HandleUiActionError("queue", "重新解析失败项失败", ex);
        }
    }

    private async Task ReResolveQueueItemsAsync(IReadOnlyList<DownloadQueueItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            item.HasError = false;
            item.IsDownloading = false;
            SetQueueItemVisualState(item, DownloadQueueState.Waiting, "等待", "重新解析中");
            _reResolveItems.Add(item);
        }

        UpdateDownloadQueueControlUi();

        if (_isDownloadingQueue)
        {
            NotifyDownloadQueueChanged();
            StatusText.Text = items.Count == 1 ? "已将 1 个失败项加入重新解析。" : $"已将 {items.Count} 个失败项加入重新解析。";
            return;
        }

        StatusText.Text = items.Count == 1 ? "正在重新解析 1 个失败项..." : $"正在重新解析 {items.Count} 个失败项...";
        await StartQueueDownloadAsync(items);
    }

    private async void ClearQueueButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MessageBox.Show(this, "确定清空下载队列吗？", "清空下载队列", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            foreach (var item in _downloadQueue.ToList())
            {
                DeleteQueueItemTemporaryFile(item);
                ClearQueueItemCancellationToken(item);
            }

            _downloadQueue.Clear();
            ResetQueueRunSummaryState();
            if (!await TrySaveDownloadQueueAsync("queue", "保存下载队列失败"))
            {
                _hasPausedQueue = false;
                UpdateDownloadQueueControlUi();
                StatusText.Text = "已清空下载队列，但队列保存失败。";
                return;
            }

            _hasPausedQueue = false;
            UpdateDownloadQueueControlUi();
            StatusText.Text = "已清空下载队列。";
        }
        catch (Exception ex)
        {
            HandleUiActionError("queue", "清空下载队列失败", ex);
        }
    }

    private async Task SearchAsync(int page)
    {
        if (_apiClient is null) InitSessionWithoutCf();

        if (string.IsNullOrWhiteSpace(_currentSearchKeyword))
        {
        }

        _isSearching = true;
        SearchButton.IsEnabled = false;
        PreviousPageButton.IsEnabled = false;
        NextPageButton.IsEnabled = false;
        ResetDetailsView("正在搜索，等待选择视频...");
        CancelPendingDetailsLoad();

        try
        {
            var apiClient = _apiClient;
            if (apiClient is null)
            {
                StatusText.Text = "浏览会话尚未初始化。";
                return;
            }

            StatusText.Text = page == 1 ? "正在搜索..." : $"正在加载第 {page} 页...";
            LogInfo("search", page == 1 ? $"开始搜索: {_currentSearchKeyword}" : $"加载搜索页: {_currentSearchKeyword} / 第 {page} 页");
            var searchPage = await apiClient.SearchAsync(_currentSearchKeyword, page, _searchFilters);
            _currentDetailsVideoId = null;
            _searchResults.Clear();
            foreach (var result in searchPage.Results)
            {
                _searchResults.Add(result);
            }
            PrimeThumbnails(_searchResults);

            _currentPage = searchPage.CurrentPage;
            _totalPages = searchPage.TotalPages;
            UpdatePageNavigationUi();
            LeftTabControl.SelectedIndex = 0;
            StatusText.Text = _searchFilters.HasActiveFilters
                ? $"搜索完成，第 {_currentPage} 页，共 {searchPage.Results.Count} 条结果，已应用筛选。"
                : $"搜索完成，第 {_currentPage} 页，共 {searchPage.Results.Count} 条结果。";
            LogInfo("search", $"搜索完成: {_currentSearchKeyword} / 第 {_currentPage} 页 / {searchPage.Results.Count} 条");
        }
        catch (Exception ex) when (IsCloudflareSessionError(ex))
        {
            var restored = await EnsureVerifiedSessionAsync("检测到 Cloudflare 会话失效，正在自动打开验证窗口...");
            if (restored)
            {
                await SearchAsync(page);
                return;
            }

            StatusText.Text = $"搜索失败: {ex.Message}";
        }
        catch (Exception ex)
        {
            _searchResults.Clear();
            StatusText.Text = ex.Message.Length > 60 ? "搜索失败：未解析到结果。" : $"搜索失败: {ex.Message}";
        }
        finally
        {
            _isSearching = false;
            SearchButton.IsEnabled = true;
            UpdatePageNavigationUi();
        }
    }

    private async Task LoadDetailsAsync(VideoSummary summary)
    {
        if (_apiClient is null) InitSessionWithoutCf();

        if (_isSearching)
        {
            return;
        }

        if (string.Equals(_currentDetailsVideoId, summary.VideoId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CancelPendingDetailsLoad();
        var cancellationTokenSource = new CancellationTokenSource();
        _detailsLoadCancellationTokenSource = cancellationTokenSource;
        var requestVersion = Interlocked.Increment(ref _detailsRequestVersion);
        var requestedVideoId = summary.VideoId;
        _isLoadingDetails = true;
        QueueSourceButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        SourcesList.ItemsSource = null;
        SourcesSummaryText.Text = "正在读取详情与视频源...";
        StatusText.Text = $"正在读取 {requestedVideoId} 的详情...";
        ResetDetailsView("正在加载视频信息...");
        SourcesSummaryText.Text = "正在读取详情与视频源...";

        try
        {
            var apiClient = _apiClient;
            if (apiClient is null)
            {
                StatusText.Text = "浏览会话尚未初始化。";
                return;
            }

            LogInfo("details", $"开始读取详情: {requestedVideoId}");
            var details = await GetOrLoadVideoDetailsAsync(requestedVideoId, cancellationToken: cancellationTokenSource.Token);
            if (cancellationTokenSource.IsCancellationRequested ||
                requestVersion != Volatile.Read(ref _detailsRequestVersion) ||
                !string.Equals((GetSelectedVideoSummary()?.VideoId) ?? requestedVideoId, requestedVideoId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (details is null)
            {
                SourcesSummaryText.Text = "详情读取失败，未拿到视频源信息。";
                StatusText.Text = "读取详情失败。";
                return;
            }

            ApplyVideoDetails(details);
            if (!string.IsNullOrWhiteSpace(details.CoverUrl))
            {
                var matched = _favoriteFolders.Values.SelectMany(f => f)
                    .FirstOrDefault(s => string.Equals(s.VideoId, details.VideoId, StringComparison.OrdinalIgnoreCase));
                if (matched is not null && string.IsNullOrWhiteSpace(matched.CoverUrl))
                {
                    matched.CoverUrl = details.CoverUrl;
                    _ = PrimeThumbnailAsync(matched);
                }
            }
            StatusText.Text = $"已读取 {details.VideoId} 的详情。";
            LogInfo("details", $"详情读取完成: {details.VideoId}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (IsCloudflareSessionError(ex))
        {
            if (requestVersion != Volatile.Read(ref _detailsRequestVersion) ||
                !string.Equals((GetSelectedVideoSummary()?.VideoId) ?? requestedVideoId, requestedVideoId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SourcesSummaryText.Text = "当前会话失效，正在等待 Cloudflare 重验...";
            var restored = await EnsureVerifiedSessionAsync("检测到 Cloudflare 会话失效，正在自动打开验证窗口...", cancellationTokenSource.Token);
            if (restored && !cancellationTokenSource.IsCancellationRequested)
            {
                SourcesSummaryText.Text = "会话已恢复，正在重新读取详情与视频源...";
                await LoadDetailsAsync(summary);
                return;
            }

            ResetDetailsView("详情加载失败，请重新选择视频。");
            SourcesSummaryText.Text = "详情加载失败，视频源未读取完成。";
            StatusText.Text = $"读取详情失败: {ex.Message}";
        }
        catch (Exception ex)
        {
            if (requestVersion != Volatile.Read(ref _detailsRequestVersion) ||
                !string.Equals((GetSelectedVideoSummary()?.VideoId) ?? requestedVideoId, requestedVideoId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ResetDetailsView("详情加载失败，请重新选择视频。");
            SourcesSummaryText.Text = "详情加载失败，视频源未读取完成。";
            StatusText.Text = $"读取详情失败: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_detailsLoadCancellationTokenSource, cancellationTokenSource))
            {
                _detailsLoadCancellationTokenSource = null;
                _isLoadingDetails = false;
                DownloadButton.IsEnabled = SourcesList.SelectedItem is VideoSource;
                QueueSourceButton.IsEnabled = SourcesList.SelectedItem is VideoSource;
            }

            cancellationTokenSource.Dispose();
        }
    }

    private VideoSummary? GetSelectedVideoSummary()
    {
        return ResultsList.SelectedItem as VideoSummary
            ?? FavoritesList.SelectedItem as VideoSummary
            ?? RelatedList.SelectedItem as VideoSummary;
    }

    private void CancelPendingDetailsLoad()
    {
        Interlocked.Increment(ref _detailsRequestVersion);
        _detailsLoadCancellationTokenSource?.Cancel();
        _detailsLoadCancellationTokenSource = null;
        _isLoadingDetails = false;
    }

    private void ResetDetailsView(string titleText = "请选择左侧视频查看详情。")
    {
        _currentDetails = null;
        _currentDetailsVideoId = null;
        TitleText.Text = titleText;
        UploadDateText.Text = "-";
        LikesText.Text = "-";
        ViewsText.Text = "-";
        DurationText.Text = "-";
        TagsText.Text = "-";
        UrlText.Text = string.Empty;
        RelatedList.ItemsSource = null;
        SourcesSummaryText.Text = "未加载视频源";
        SourcesList.ItemsSource = null;
        SourcesList.SelectedItem = null;
        ViewDescriptionButton.IsEnabled = false;
        PreviewCoverButton.IsEnabled = false;
        CopyUrlButton.IsEnabled = false;
        OpenVideoPageButton.IsEnabled = false;
        UpdateFavoriteButtonState();
        QueueSourceButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
    }

    private void ApplyVideoDetails(VideoDetails details)
    {
        _currentDetails = details;
        _currentDetailsVideoId = details.VideoId;
        TitleText.Text = SimplifiedChineseConverter.ToSimplified(details.Title);
        UploadDateText.Text = string.IsNullOrWhiteSpace(details.UploadDate) ? "-" : SimplifiedChineseConverter.ToSimplified(details.UploadDate);
        LikesText.Text = string.IsNullOrWhiteSpace(details.Likes) ? "-" : SimplifiedChineseConverter.ToSimplified(details.Likes);
        ViewsText.Text = string.IsNullOrWhiteSpace(details.Views) ? "-" : SimplifiedChineseConverter.ToSimplified(details.Views);
        DurationText.Text = string.IsNullOrWhiteSpace(details.Duration) ? "-" : SimplifiedChineseConverter.ToSimplified(details.Duration);
        TagsText.Text = details.Tags.Count == 0 ? "-" : string.Join(" / ", details.Tags.Select(SimplifiedChineseConverter.ToSimplified));
        UrlText.Text = details.Url;
        RelatedList.ItemsSource = details.RelatedVideos;
        PrimeThumbnails(details.RelatedVideos);
        SourcesSummaryText.Text = BuildSourcesSummaryText(details);
        SourcesList.ItemsSource = details.Sources;
        SourcesList.SelectedItem = details.Sources.FirstOrDefault();
        ViewDescriptionButton.IsEnabled = !string.IsNullOrWhiteSpace(details.Description);
        PreviewCoverButton.IsEnabled = !string.IsNullOrWhiteSpace(details.CoverUrl);
        CopyUrlButton.IsEnabled = !string.IsNullOrWhiteSpace(details.Url);
        OpenVideoPageButton.IsEnabled = !string.IsNullOrWhiteSpace(details.Url);
        UpdateFavoriteButtonState(details.VideoId);
        QueueSourceButton.IsEnabled = SourcesList.SelectedItem is VideoSource;
        DownloadButton.IsEnabled = SourcesList.SelectedItem is VideoSource;
        ApplyVideoDetailsVisibility();
    }

    private async Task ShowVideoDetailsDialogAsync(VideoSummary summary)
    {
        if (_apiClient is null)
        {
            InitSessionWithoutCf();
        }

        var details = await GetOrLoadVideoDetailsAsync(summary.VideoId, GetActiveVideoDetailsLoadOptions());
        if (details is null)
        {
            StatusText.Text = "读取详情失败。";
            return;
        }

        var titleText = SimplifiedChineseConverter.ToSimplified(details.Title);
        var uploadDateText = string.IsNullOrWhiteSpace(details.UploadDate) ? "-" : SimplifiedChineseConverter.ToSimplified(details.UploadDate);
        var likesText = string.IsNullOrWhiteSpace(details.Likes) ? "-" : SimplifiedChineseConverter.ToSimplified(details.Likes);
        var viewsText = string.IsNullOrWhiteSpace(details.Views) ? "-" : SimplifiedChineseConverter.ToSimplified(details.Views);
        var durationText = string.IsNullOrWhiteSpace(details.Duration) ? "-" : SimplifiedChineseConverter.ToSimplified(details.Duration);
        var tagsText = details.Tags.Count == 0 ? "-" : string.Join(" / ", details.Tags.Select(SimplifiedChineseConverter.ToSimplified));

        var window = new Window
        {
            Owner = this,
            Title = $"视频信息 - {titleText}",
            Width = 680,
            Height = 420,
            MinWidth = 520,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AppThemeService.GetBrush("ThemeSurfaceBrush")
        };

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.PreviewMouseLeftButtonDown += (_, e) => TryStartDialogDrag(window, e);

        var headerBorder = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.SizeAll
        };

        var header = new DockPanel { LastChildFill = false };
        header.Children.Add(new TextBlock
        {
            Text = "信息",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppThemeService.GetBrush("ThemeTextBrush")
        });
        header.Children.Add(new TextBlock
        {
            Margin = new Thickness(10, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AppThemeService.GetBrush("ThemeTextMutedBrush"),
            FontSize = 10,
            Text = "基础信息"
        });
        DockPanel.SetDock(header.Children[^1], Dock.Left);

        var coverButton = new Button
        {
            Width = 64,
            Height = 24,
            Content = "查看封面",
            IsEnabled = !string.IsNullOrWhiteSpace(details.CoverUrl),
            Visibility = _settings.VideoDetailsVisibility.Cover ? Visibility.Visible : Visibility.Collapsed
        };
        coverButton.Click += async (_, _) =>
        {
            try
            {
                await ShowCoverPreviewAsync(details);
            }
            catch (Exception ex)
            {
                HandleUiActionError("cover", "查看封面失败", ex);
            }
        };
        DockPanel.SetDock(coverButton, Dock.Right);
        header.Children.Add(coverButton);
        headerBorder.Child = header;
        root.Children.Add(headerBorder);

        var infoBorder = new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(10, 8, 10, 8),
            Background = AppThemeService.GetBrush("ThemeSurfaceAltBrush"),
            BorderBrush = AppThemeService.GetBrush("ThemeBorderAltBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2)
        };
        Grid.SetRow(infoBorder, 1);

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var visibility = _settings.VideoDetailsVisibility;
        var visibleRows = new List<(string Label, string Value)>();
        if (visibility.Title)
        {
            visibleRows.Add(("标题", titleText));
        }
        if (visibility.UploadDate)
        {
            visibleRows.Add(("上传时间", uploadDateText));
        }
        if (visibility.Likes)
        {
            visibleRows.Add(("点赞", likesText));
        }
        if (visibility.Views)
        {
            visibleRows.Add(("观看", viewsText));
        }
        if (visibility.Duration)
        {
            visibleRows.Add(("时长", durationText));
        }
        if (visibility.Tags)
        {
            visibleRows.Add(("标签", tagsText));
        }
        if (visibleRows.Count == 0)
        {
            visibleRows.Add(("提示", "当前设置未启用详情字段"));
        }

        var infoGrid = new Grid();
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < visibleRows.Count; i++)
        {
            infoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddDetailRow(infoGrid, i, visibleRows[i].Label, visibleRows[i].Value);
        }
        scrollViewer.Content = infoGrid;
        infoBorder.Child = scrollViewer;
        root.Children.Add(infoBorder);

        window.Content = root;
        window.ShowDialog();
        StatusText.Text = $"已查看 {details.VideoId} 的详情。";
    }

    private static void TryStartDialogDrag(Window window, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1 || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button || current is ScrollBar)
            {
                return;
            }
        }

        window.DragMove();
        e.Handled = true;
    }

    private static void AddDetailRow(Grid grid, int row, string label, string value, bool wrap = true)
    {
        var labelText = new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 2, 10, 2),
            Foreground = AppThemeService.GetBrush("ThemeTextSubtleBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(labelText, row);
        grid.Children.Add(labelText);

        var valueText = new TextBlock
        {
            Text = value,
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = AppThemeService.GetBrush("ThemeTextBrush"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(valueText, row);
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
    }

    private UIElement CreateVideoSourceDialogRow(VideoSource source, VideoDetails details)
    {
        var border = new Border
        {
            Padding = new Thickness(3, 1, 3, 1),
            Background = AppThemeService.GetBrush("ThemeSurfaceBrush"),
            BorderBrush = AppThemeService.GetBrush("ThemeBorderSubtleBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = source.QualityText,
            Foreground = AppThemeService.GetBrush("ThemeTextBrush"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = source.TypeText,
            Margin = new Thickness(0, 1, 0, 0),
            Foreground = AppThemeService.GetBrush("ThemeTextMutedBrush"),
            FontSize = 10
        });
        grid.Children.Add(stack);

        var playButton = new Button
        {
            Content = "播放",
            Height = 20,
            Tag = source
        };
        Grid.SetColumn(playButton, 1);
        playButton.Click += async (_, _) =>
        {
            var player = new Views.PlayerWindow(_settings) { Owner = this };
            await player.OpenAsync(details.Title, source.Url, source.Type);
        };
        grid.Children.Add(playButton);

        var queueButton = new Button
        {
            Content = "队列",
            Height = 20,
            Tag = source
        };
        Grid.SetColumn(queueButton, 3);
        queueButton.Click += (_, _) =>
        {
            _currentDetails = details;
            _currentDetailsVideoId = details.VideoId;
            QueueVideoSource(source);
        };
        grid.Children.Add(queueButton);

        var downloadButton = new Button
        {
            Content = "下载",
            Height = 20,
            Tag = source
        };
        Grid.SetColumn(downloadButton, 5);
        downloadButton.Click += (_, _) =>
        {
            _currentDetails = details;
            _currentDetailsVideoId = details.VideoId;
            QueueVideoSource(source, startImmediately: true);
        };
        grid.Children.Add(downloadButton);

        border.Child = grid;
        return border;
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
            _settings.CompactMode = loaded.CompactMode;
            _settings.DefaultQuality = string.IsNullOrWhiteSpace(loaded.DefaultQuality) ? _settings.DefaultQuality : loaded.DefaultQuality;
            _settings.ThemeMode = AppThemeService.Normalize(loaded.ThemeMode);
            _settings.SiteHost = string.IsNullOrWhiteSpace(loaded.SiteHost) ? _settings.SiteHost : loaded.SiteHost;
            _settings.CustomSiteHosts = loaded.CustomSiteHosts?.Where(host => !string.IsNullOrWhiteSpace(host)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
            _settings.PersistDownloadQueue = loaded.PersistDownloadQueue;
            _settings.MaxConcurrentDownloads = Math.Clamp(loaded.MaxConcurrentDownloads, 1, 3);
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
            return fullPath;
        }
        catch
        {
            return AppSettings.DefaultDownloadPath;
        }
    }

    private string GetCookieCacheFilePath()
    {
        var host = string.IsNullOrWhiteSpace(_settings.SiteHost) ? "default" : _settings.SiteHost.Trim().ToLowerInvariant();
        host = host.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        return Path.Combine(AppDataDir, $"cookies.{host}.json");
    }

    private async Task SaveCookieCacheAsync()
    {
        await File.WriteAllTextAsync(GetCookieCacheFilePath(), JsonSerializer.Serialize(_appState.Cookies, FavoritesJsonOptions));
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
        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(_settings, FavoritesJsonOptions));
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
    }

    private static double NormalizeFiniteOrDefault(double value, double fallback)
    {
        return double.IsFinite(value) && value > 0 ? value : fallback;
    }

    private static double? NormalizeFiniteOrNull(double? value)
    {
        return value.HasValue && double.IsFinite(value.Value) ? value.Value : null;
    }

    private void LoadFavorites()
    {
        _favoriteFolders.Clear();

        if (!File.Exists(FavoritesFilePath))
        {
            _favoriteFolders[DefaultFavoritesFolder] = [];
            return;
        }

        try
        {
            var importedFolders = ParseFavoriteImport(File.ReadAllText(FavoritesFilePath));
            foreach (var (folderName, videos) in importedFolders)
            {
                _favoriteFolders[folderName] = new ObservableCollection<VideoSummary>(videos);
            }
        }
        catch (Exception ex)
        {
            LogError("startup", $"读取收藏夹失败: {FavoritesFilePath}", ex);
            AddStartupWarning("收藏夹读取失败，已使用空收藏夹");
            _favoriteFolders.Clear();
        }

        if (_favoriteFolders.Count == 0)
        {
            _favoriteFolders[DefaultFavoritesFolder] = [];
        }
    }

    private void LoadDownloadHistory()
    {
        _historyItems.Clear();
        if (!File.Exists(DownloadHistoryFilePath))
        {
            return;
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(DownloadHistoryFilePath)) ?? [];
            foreach (var item in items.Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                _historyItems.Add(DownloadHistoryItem.FromRawText(item));
            }
        }
        catch (Exception ex)
        {
            LogError("startup", $"读取下载历史失败: {DownloadHistoryFilePath}", ex);
            AddStartupWarning("下载历史读取失败，已清空历史列表");
            _historyItems.Clear();
        }
    }

    private void LoadDownloadQueue()
    {
        _downloadQueue.Clear();
        if (!File.Exists(DownloadQueueFilePath))
        {
            return;
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<DownloadQueueRecord>>(File.ReadAllText(DownloadQueueFilePath)) ?? [];
            foreach (var item in items.Where(item => !string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(item.VideoId)))
            {
                _downloadQueue.Add(new DownloadQueueItem
                {
                    Title = item.Title,
                    Url = item.Url,
                    Type = string.IsNullOrWhiteSpace(item.Type) ? "mp4" : item.Type,
                    Quality = item.Quality,
                    VideoId = item.VideoId,
                    TargetPath = item.TargetPath,
                    StageText = "等待",
                    QueueStatusText = "等待中"
                });
            }
        }
        catch (Exception ex)
        {
            LogError("startup", $"读取下载队列失败: {DownloadQueueFilePath}", ex);
            AddStartupWarning("下载队列读取失败，已清空队列列表");
            _downloadQueue.Clear();
        }
    }

    private void SaveFavorites()
    {
        var exportData = _favoriteFolders.ToDictionary(
            item => item.Key,
            item => GetFavoriteRecords(item.Value),
            StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(FavoritesFilePath, JsonSerializer.Serialize(exportData, FavoritesJsonOptions));
    }

    private async Task SaveDownloadHistoryAsync()
    {
        await File.WriteAllTextAsync(DownloadHistoryFilePath, JsonSerializer.Serialize(_historyItems.Select(item => item.RawText), FavoritesJsonOptions));
    }

    private bool TrySaveFavorites(string category, string failureMessage)
    {
        try
        {
            SaveFavorites();
            return true;
        }
        catch (Exception ex)
        {
            LogError(category, failureMessage, ex);
            return false;
        }
    }

    private async Task<bool> TrySaveDownloadHistoryAsync(string category, string failureMessage)
    {
        try
        {
            await SaveDownloadHistoryAsync();
            return true;
        }
        catch (Exception ex)
        {
            LogError(category, failureMessage, ex);
            return false;
        }
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

    private async Task SaveDownloadQueueAsync()
    {
        if (!_settings.PersistDownloadQueue)
        {
            if (File.Exists(DownloadQueueFilePath))
            {
                File.Delete(DownloadQueueFilePath);
            }
            return;
        }

        var items = _downloadQueue.Select(item => new DownloadQueueRecord
        {
            Title = item.Title,
            Url = item.Url,
            Type = item.Type,
            Quality = item.Quality,
            VideoId = item.VideoId,
            TargetPath = item.TargetPath
        }).ToList();
        await File.WriteAllTextAsync(DownloadQueueFilePath, JsonSerializer.Serialize(items, FavoritesJsonOptions));
    }

    private async Task<bool> TrySaveDownloadQueueAsync(string category, string failureMessage)
    {
        try
        {
            await SaveDownloadQueueAsync();
            return true;
        }
        catch (Exception ex)
        {
            LogError(category, failureMessage, ex);
            return false;
        }
    }

    private IReadOnlyList<BrowserCookieRecord> ParseImportedCookies(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var trimmed = content.Trim();
        if (!trimmed.StartsWith("{") && !trimmed.StartsWith("["))
        {
            return ParseCookieHeaderText(trimmed);
        }

        using var document = JsonDocument.Parse(trimmed);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            var cookies = JsonSerializer.Deserialize<List<BrowserCookieRecord>>(trimmed) ?? [];
            return cookies.Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name) && !string.IsNullOrWhiteSpace(cookie.Value)).ToList();
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (document.RootElement.TryGetProperty("cookies", out var cookiesElement) && cookiesElement.ValueKind == JsonValueKind.Array)
            {
                var cookies = JsonSerializer.Deserialize<List<BrowserCookieRecord>>(cookiesElement.GetRawText()) ?? [];
                return cookies.Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name) && !string.IsNullOrWhiteSpace(cookie.Value)).ToList();
            }

            return ParseCookieHeaderText(string.Join("; ", document.RootElement.EnumerateObject().Select(property => $"{property.Name}={property.Value.GetString()}")));
        }

        return [];
    }

    private IReadOnlyList<BrowserCookieRecord> ParseCookieHeaderText(string cookieHeader)
    {
        return cookieHeader
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
            .Select(parts => new BrowserCookieRecord
            {
                Name = parts[0].Trim(),
                Value = parts[1].Trim(),
                Domain = $".{_settings.SiteHost}",
                Path = "/",
                IsSecure = true,
                IsHttpOnly = false
            })
            .ToList();
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

    private List<VideoSummary> ParseFavoriteVideos(JsonElement array)
    {
        var videos = new List<VideoSummary>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var videoId = ReadJsonString(item, "video_id", "videoId", "VideoId");
            var title = ReadJsonString(item, "title", "Title");
            var url = ReadJsonString(item, "url", "Url");
            if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            videos.Add(new VideoSummary
            {
                VideoId = videoId,
                Title = SimplifiedChineseConverter.ToSimplified(title),
                Url = string.IsNullOrWhiteSpace(url) ? $"https://{_settings.SiteHost}/watch?v={videoId}" : url,
                CoverUrl = ReadJsonString(item, "cover_url", "coverUrl", "CoverUrl")
            });
        }

        return videos;
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

    private static List<FavoriteVideoRecord> GetFavoriteRecords(IEnumerable<VideoSummary> videos)
    {
        return videos
            .Select(video => new FavoriteVideoRecord
            {
                VideoId = video.VideoId,
                Title = video.Title,
                Url = video.Url,
                CoverUrl = video.CoverUrl
            })
            .ToList();
    }

    private string? PromptForFolderName(string title, string prompt, string? defaultValue = null)
    {
        var dialog = new InputDialog(title, prompt, defaultValue) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.InputText.Trim() : null;
    }

    private void FetchMissingCovers(IEnumerable<VideoSummary> videos)
    {
        if (_apiClient is null) return;
        var missing = videos.Where(v => string.IsNullOrWhiteSpace(v.CoverUrl)).ToList();
        if (missing.Count == 0) return;
        _ = FetchMissingCoversAsync(missing);
    }

    private async Task FetchMissingCoversAsync(List<VideoSummary> videos)
    {
        using var gate = new SemaphoreSlim(2, 2);
        await Task.WhenAll(videos.Select(async summary =>
        {
            await gate.WaitAsync();
            try
            {
                var details = await GetOrLoadVideoDetailsAsync(summary.VideoId, VideoDetailsLoadOptions.Cover);
                if (string.IsNullOrWhiteSpace(details?.CoverUrl)) return;
                summary.CoverUrl = details.CoverUrl;
                _ = PrimeThumbnailAsync(summary);
            }
            finally
            {
                gate.Release();
            }
        }));
        TrySaveFavorites("favorites", "保存收藏夹失败");
    }

    private string? ResolveImportFolderName(string folderName)
    {
        if (!_favoriteFolders.ContainsKey(folderName))
        {
            return folderName;
        }

        var messageBoxResult = MessageBox.Show(
            this,
            $"收藏夹 '{folderName}' 已存在。\n选择“是”合并，选择“否”后输入新名称，选择“取消”放弃导入。",
            "导入收藏夹",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (messageBoxResult == MessageBoxResult.Yes)
        {
            return folderName;
        }

        if (messageBoxResult == MessageBoxResult.Cancel)
        {
            return null;
        }

        while (true)
        {
            var renamedFolder = PromptForFolderName("重命名导入收藏夹", "请输入新的收藏夹名称：", $"{folderName}_导入");
            if (renamedFolder is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(renamedFolder))
            {
                continue;
            }

            if (_favoriteFolders.ContainsKey(renamedFolder))
            {
                MessageBox.Show(this, $"收藏夹 {renamedFolder} 已存在，请重新输入。", "导入收藏夹", MessageBoxButton.OK, MessageBoxImage.Information);
                continue;
            }

            return renamedFolder;
        }
    }

    private void RefreshFavoriteFolders(string? selectedFolder = null)
    {
        var currentFolder = selectedFolder ?? FavoritesFolderBox.SelectedItem as string ?? DefaultFavoritesFolder;
        FavoritesFolderBox.ItemsSource = null;
        FavoritesFolderBox.ItemsSource = _favoriteFolders.Keys.ToList();
        FavoritesFolderBox.SelectedItem = _favoriteFolders.ContainsKey(currentFolder) ? currentFolder : DefaultFavoritesFolder;
    }

    private void RefreshFavoritesView()
    {
        var folderName = FavoritesFolderBox.SelectedItem as string ?? DefaultFavoritesFolder;
        if (!_favoriteFolders.TryGetValue(folderName, out var favorites))
        {
            FavoritesList.ItemsSource = Array.Empty<VideoSummary>();
            _favoritesView = null;
            return;
        }

        FavoritesList.ItemsSource = favorites;
        _favoritesView = CollectionViewSource.GetDefaultView(FavoritesList.ItemsSource);
        if (_favoritesView is not null)
        {
            _favoritesView.Filter = MatchesFavoriteFilter;
            _favoritesView.Refresh();
        }
    }

    private void RefreshHistoryView()
    {
        if (HistoryList.ItemsSource != _historyItems)
        {
            HistoryList.ItemsSource = _historyItems;
        }

        _historyView = CollectionViewSource.GetDefaultView(HistoryList.ItemsSource);
        if (_historyView is not null)
        {
            _historyView.Filter = MatchesHistoryFilter;
            _historyView.Refresh();
        }
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

    private void HistorySearchBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RestartFilterTimer(_historyFilterTimer);
    }

    private bool MatchesFavoriteFilter(object item)
    {
        if (item is not VideoSummary summary)
        {
            return false;
        }

        var keyword = FavoritesSearchBox.Text.Trim();
        return string.IsNullOrWhiteSpace(keyword) ||
               summary.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               summary.VideoId.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesHistoryFilter(object item)
    {
        if (item is not DownloadHistoryItem historyItem)
        {
            return false;
        }

        var keyword = HistorySearchBox.Text.Trim();
        return string.IsNullOrWhiteSpace(keyword) ||
               historyItem.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               historyItem.RawText.Contains(keyword, StringComparison.OrdinalIgnoreCase);
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

        foreach (var item in items)
        {
            _ = PrimeThumbnailAsync(item);
        }
    }

    private void FavoritesList_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
    }

    private void UpdatePageNavigationUi()
    {
        PageNavigationLabel.Text = $"{_currentPage} / {_totalPages}";
        FirstPageButton.IsEnabled = _currentPage > 1;
        PreviousPageButton.IsEnabled = _currentPage > 1;
        NextPageButton.IsEnabled = _currentPage < _totalPages;
        LastPageButton.IsEnabled = _currentPage < _totalPages;

        var pageButtons = new[] { PageButton1, PageButton2, PageButton3, PageButton4, PageButton5 };
        var startPage = Math.Max(1, _currentPage - 2);
        var endPage = Math.Min(_totalPages, startPage + pageButtons.Length - 1);
        if (endPage - startPage + 1 < pageButtons.Length)
        {
            startPage = Math.Max(1, endPage - pageButtons.Length + 1);
        }

        for (var index = 0; index < pageButtons.Length; index++)
        {
            var button = pageButtons[index];
            var page = startPage + index;
            var visible = page <= endPage;
            button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
            {
                button.Tag = null;
                continue;
            }

            button.Tag = page;
            button.Content = page.ToString();
            button.IsEnabled = page != _currentPage;
            button.Style = (Style)FindResource(page == _currentPage ? "PrimaryActionButtonStyle" : "SecondaryActionButtonStyle");
        }
    }

    private void UpdateFilterSummaryUi()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_searchFilters.Genre)) parts.Add($"类型: {SimplifiedChineseConverter.ToSimplified(_searchFilters.Genre)}");
        if (!string.IsNullOrWhiteSpace(_searchFilters.Sort)) parts.Add($"排序: {SimplifiedChineseConverter.ToSimplified(_searchFilters.Sort)}");
        if (!string.IsNullOrWhiteSpace(_searchFilters.Date)) parts.Add($"日期: {SimplifiedChineseConverter.ToSimplified(_searchFilters.Date)}");
        if (!string.IsNullOrWhiteSpace(_searchFilters.Duration)) parts.Add($"时长: {SimplifiedChineseConverter.ToSimplified(_searchFilters.Duration)}");
        if (_searchFilters.Tags.Count > 0) parts.Add($"标签: {string.Join("、", _searchFilters.Tags.Take(3).Select(SimplifiedChineseConverter.ToSimplified))}{(_searchFilters.Tags.Count > 3 ? $" 等{_searchFilters.Tags.Count}项" : string.Empty)}");
        if (_searchFilters.Broad) parts.Add("广泛配对");

        var hasFilters = parts.Count > 0;
        ActiveFilterSummaryPanel.Visibility = hasFilters ? Visibility.Visible : Visibility.Collapsed;
        ActiveFilterCountText.Text = $"{parts.Count} 项";
        ActiveFilterSummaryText.Text = hasFilters ? string.Join("  ·  ", parts) : string.Empty;
        ClearFiltersButton.IsEnabled = hasFilters;
    }

    private void ApplyCompactMode()
    {
        var v = _settings.CompactMode ? Visibility.Collapsed : Visibility.Visible;
        InfoPanel.Visibility = v;
        if (_settings.CompactMode)
        {
            InfoColumn.Width = new GridLength(0);
            InfoColumn.MinWidth = 0;
        }
        else
        {
            InfoColumn.Width = new GridLength(1.4, GridUnitType.Star);
            InfoColumn.MinWidth = 374;
        }
    }

    private void ApplyVideoDetailsVisibility()
    {
        TitleText.Visibility = _settings.VideoDetailsVisibility.Title ? Visibility.Visible : Visibility.Collapsed;
        UploadDateLabel.Visibility = _settings.VideoDetailsVisibility.UploadDate ? Visibility.Visible : Visibility.Collapsed;
        UploadDateText.Visibility = _settings.VideoDetailsVisibility.UploadDate ? Visibility.Visible : Visibility.Collapsed;
        LikesLabel.Visibility = _settings.VideoDetailsVisibility.Likes ? Visibility.Visible : Visibility.Collapsed;
        LikesText.Visibility = _settings.VideoDetailsVisibility.Likes ? Visibility.Visible : Visibility.Collapsed;
        ViewsLabel.Visibility = _settings.VideoDetailsVisibility.Views ? Visibility.Visible : Visibility.Collapsed;
        ViewsText.Visibility = _settings.VideoDetailsVisibility.Views ? Visibility.Visible : Visibility.Collapsed;
        DurationLabel.Visibility = _settings.VideoDetailsVisibility.Duration ? Visibility.Visible : Visibility.Collapsed;
        DurationText.Visibility = _settings.VideoDetailsVisibility.Duration ? Visibility.Visible : Visibility.Collapsed;
        TagsLabel.Visibility = _settings.VideoDetailsVisibility.Tags ? Visibility.Visible : Visibility.Collapsed;
        TagsText.Visibility = _settings.VideoDetailsVisibility.Tags ? Visibility.Visible : Visibility.Collapsed;
        PreviewCoverButton.Visibility = _settings.VideoDetailsVisibility.Cover ? Visibility.Visible : Visibility.Collapsed;
        RelatedList.Visibility = _settings.VideoDetailsVisibility.RelatedVideos ? Visibility.Visible : Visibility.Collapsed;
    }

    private string BuildSourcesSummaryText(VideoDetails details)
    {
        var basicLoaded = details.LoadOptions.HasFlag(VideoDetailsLoadOptions.Basic);
        var sourcesLoaded = details.LoadOptions.HasFlag(VideoDetailsLoadOptions.Sources);

        if (!basicLoaded)
        {
            return "正在读取详情...";
        }

        if (!sourcesLoaded)
        {
            return "详情已加载，未请求视频源。";
        }

        if (details.Sources.Count == 0)
        {
            return "详情已加载，但当前没有解析到可用视频源。";
        }

        var sourceTypes = details.Sources
            .Select(source => source.TypeText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        var typeSuffix = sourceTypes.Count == 0 ? string.Empty : $"，类型 {string.Join("/", sourceTypes)}";
        return $"详情已加载，共 {details.Sources.Count} 个视频源{typeSuffix}";
    }

    private static SearchFilterOptions CloneSearchFilters(SearchFilterOptions options)
    {
        return new SearchFilterOptions
        {
            Genre = options.Genre,
            Sort = options.Sort,
            Date = options.Date,
            Duration = options.Duration,
            Tags = options.Tags.ToList(),
            Broad = options.Broad
        };
    }

    private Task RefreshSearchAfterFilterChangeAsync(SearchFilterOptions previousFilters)
    {
        if (AreFiltersEqual(previousFilters, _searchFilters))
        {
            StatusText.Text = _searchFilters.HasActiveFilters ? "筛选条件未变化。" : "当前未启用筛选。";
            return Task.CompletedTask;
        }

        StatusText.Text = _searchFilters.HasActiveFilters ? "已更新筛选条件，请手动重新搜索。" : "已清空筛选条件。";
        return Task.CompletedTask;
    }

    private static bool AreFiltersEqual(SearchFilterOptions left, SearchFilterOptions right)
    {
        return string.Equals(left.Genre, right.Genre, StringComparison.Ordinal) &&
               string.Equals(left.Sort, right.Sort, StringComparison.Ordinal) &&
               string.Equals(left.Date, right.Date, StringComparison.Ordinal) &&
               string.Equals(left.Duration, right.Duration, StringComparison.Ordinal) &&
               left.Broad == right.Broad &&
               left.Tags.SequenceEqual(right.Tags);
    }

    private async Task<bool> DownloadSourceAsync(VideoSource source, string? title, CancellationToken cancellationToken = default, DownloadQueueItem? queueItem = null, bool isRetry = false)
    {
        if (_downloadService is null)
        {
            InitSessionWithoutCf();
        }

        var extension = source.Type.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ? ".m3u8" : ".mp4";
        var downloadDirectory = EnsureDownloadDirectory();
        var videoId = queueItem?.VideoId;
        if (string.IsNullOrWhiteSpace(videoId))
        {
            videoId = _currentDetailsVideoId;
        }

        var targetPath = CreateQueueTargetPath(title, source.Type, videoId, source.Quality);
        if (queueItem is not null && !string.IsNullOrWhiteSpace(queueItem.TargetPath))
        {
            try
            {
                targetPath = DownloadPathGuard.EnsureWithinDirectory(downloadDirectory, queueItem.TargetPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                LogInfo("download", $"队列目标路径不在下载目录内，已重新生成: {queueItem.TargetPath}; {ex.Message}");
            }
        }
        if (queueItem is not null && !string.Equals(queueItem.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            queueItem.TargetPath = targetPath;
        }

        try
        {
            var downloadService = _downloadService;
            if (downloadService is null)
            {
                StatusText.Text = "下载会话尚未初始化。";
                return false;
            }

            // skip if already fully downloaded (no .tmp means not a resume)
            if (File.Exists(targetPath) && !File.Exists(targetPath + ".tmp"))
            {
                if (queueItem is not null)
                {
                    SetQueueItemVisualState(queueItem, DownloadQueueState.Completed, "完成", "已存在", showProgress: true, progressValue: 100);
                }
                StatusText.Text = $"文件已存在，跳过: {targetPath}";
                LogInfo("download", $"文件已存在，跳过: {targetPath}");
                return true;
            }

            if (queueItem is not null)
            {
                SetQueueItemVisualState(queueItem, DownloadQueueState.Checking, "检查", "检查链接", showProgress: true, isProgressIndeterminate: true);
                _queueRunCurrentTitle = queueItem.Title;
                _queueRunCurrentProgress = 0;
                UpdateQueueRuntimeSummaryUi();
            }
            if (queueItem is not null)
            {
                SetQueueItemVisualState(queueItem, DownloadQueueState.Downloading, "下载", "准备下载", showProgress: true, progressValue: 0);
                _queueRunCurrentProgress = 0;
                UpdateQueueRuntimeSummaryUi();
            }

            StatusText.Text = $"正在下载到: {targetPath}";
            var lastQueueProgressText = string.Empty;
            var lastProgressBucket = -1;
            var progress = new Progress<DownloadProgressInfo>(info =>
            {
                var speedText = info.BytesPerSecond > 0 ? $"{FormatBytes((long)info.BytesPerSecond)}/s" : string.Empty;
                var remainingText = info.EstimatedRemaining is TimeSpan remaining ? $"剩余 {FormatDuration(remaining)}" : string.Empty;

                if (queueItem is not null)
                {
                    var queueStatusText = info.Percentage is double q
                        ? $"下载中 {q:0.0}% {speedText} {remainingText}".Trim()
                        : $"下载中 {FormatBytes(info.BytesReceived)} {speedText}".Trim();
                    if (!string.Equals(queueItem.QueueStatusText, queueStatusText, StringComparison.Ordinal) &&
                        !string.Equals(lastQueueProgressText, queueStatusText, StringComparison.Ordinal))
                    {
                        queueItem.QueueStatusText = queueStatusText;
                        lastQueueProgressText = queueStatusText;
                    }

                    queueItem.StageText = "下载";
                    queueItem.ShowProgress = true;
                    queueItem.IsProgressIndeterminate = info.Percentage is null;
                    queueItem.ProgressValue = info.Percentage;
                    _queueRunCurrentProgress = info.Percentage ?? 0;
                    UpdateQueueRuntimeSummaryUi();
                }

                if (info.Percentage is double percentage)
                {
                    var bucket = (int)(percentage * 2);
                    if (bucket == lastProgressBucket)
                    {
                        return;
                    }

                    lastProgressBucket = bucket;
                    return;
                }

            });

            await downloadService.DownloadAsync(source.Url, targetPath, progress, cancellationToken);
            if (queueItem is not null)
            {
                SetQueueItemVisualState(queueItem, DownloadQueueState.Finalizing, "收尾", "写入历史", showProgress: true, progressValue: 100);
                _queueRunCurrentProgress = 100;
                UpdateQueueRuntimeSummaryUi();
            }
            var historyUrl = !string.IsNullOrWhiteSpace(queueItem?.VideoId)
                ? $"https://{_settings.SiteHost}/watch?v={queueItem.VideoId}"
                : source.Url;
            _historyItems.Insert(0, DownloadHistoryItem.Create(DateTime.Now, Path.GetFileName(targetPath), historyUrl, targetPath));
            LogInfo("download", $"下载完成: {targetPath}");
            if (!await TrySaveDownloadHistoryAsync("history", "保存下载历史失败"))
            {
                RefreshHistoryView();
                StatusText.Text = $"下载完成: {targetPath}，但历史保存失败。";
                return true;
            }

            RefreshHistoryView();
            StatusText.Text = $"下载完成: {targetPath}";
            return true;
        }
        catch (OperationCanceledException)
        {
            if (queueItem is not null)
            {
                SetQueueItemVisualState(queueItem, DownloadQueueState.Paused, "暂停", "已暂停", showProgress: true, progressValue: queueItem.ProgressValue);
                _queueRunCurrentProgress = queueItem.ProgressValue ?? _queueRunCurrentProgress;
                UpdateQueueRuntimeSummaryUi();
            }
            StatusText.Text = "下载已暂停。";
            LogInfo("download", $"下载已暂停: {targetPath}");
            return false;
        }
        catch (Exception ex) when (IsCloudflareSessionError(ex))
        {
            if (queueItem is not null)
            {
                SetQueueItemVisualState(queueItem, DownloadQueueState.Verifying, "重验", "等待重验", showProgress: true, isProgressIndeterminate: true);
                _queueRunCurrentProgress = 0;
                UpdateQueueRuntimeSummaryUi();
            }
            var restored = await EnsureVerifiedSessionAsync("下载过程中检测到 Cloudflare 会话失效，正在自动恢复...", cancellationToken);
            if (!isRetry && restored && queueItem is not null)
            {
                var refreshed = await ResolveQueueItemSourceAsync(queueItem, cancellationToken);
                if (refreshed is not null)
                {
                    return await DownloadSourceAsync(refreshed, queueItem.Title, cancellationToken, queueItem, isRetry: true);
                }
            }
            StatusText.Text = $"下载失败: {ex.Message}";
            LogError("download", $"下载失败: {targetPath}", ex);
            return false;
        }
        catch (Exception ex)
        {
            if (queueItem is not null)
            {
                SetQueueItemVisualState(queueItem, DownloadQueueState.Error, "失败", "下载失败", progressValue: null);
                _queueRunCurrentProgress = 0;
                UpdateQueueRuntimeSummaryUi();
            }
            StatusText.Text = $"下载失败: {ex.Message}";
            LogError("download", $"下载失败: {targetPath}", ex);
            return false;
        }
    }

    private async void PreviewCoverButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentDetails is null)
        {
            StatusText.Text = "当前详情没有可用封面。";
            return;
        }

        try
        {
            await ShowCoverPreviewAsync(_currentDetails);
        }
        catch (Exception ex)
        {
            HandleUiActionError("cover", "查看封面失败", ex);
        }
    }

    private async Task ShowCoverPreviewAsync(VideoDetails details)
    {
        if (string.IsNullOrWhiteSpace(details.CoverUrl) || !Uri.TryCreate(details.CoverUrl, UriKind.Absolute, out _))
        {
            StatusText.Text = "当前详情没有可用封面。";
            return;
        }

        StatusText.Text = "正在加载封面...";
        var bitmap = await ThumbnailCacheService.GetAsync(details.CoverUrl, 1280);
        if (bitmap is null)
        {
            StatusText.Text = "封面加载失败。";
            return;
        }

        var image = new System.Windows.Controls.Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            MaxWidth = 640,
            MaxHeight = 480
        };
        var window = new Window
        {
            Style = null,
            Owner = this,
            ShowInTaskbar = false,
            Title = details.Title,
            Width = 720,
            Height = 560,
            MinWidth = 520,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Black,
            Content = new System.Windows.Controls.Border
            {
                Background = Brushes.Black,
                Padding = new Thickness(12),
                Child = image
            }
        };
        window.ClearValue(Window.IconProperty);
        window.ShowDialog();
        StatusText.Text = "已打开封面预览。";
    }

    private void ViewDescriptionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentDetails is null)
        {
            MessageBox.Show(this, "请先选择一个视频。", "暂无内容", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var description = string.IsNullOrWhiteSpace(_currentDetails.Description)
            ? "暂无简介。"
            : SimplifiedChineseConverter.ToSimplified(_currentDetails.Description);
        var window = new Window
        {
            Owner = this,
            Title = $"简介 - {SimplifiedChineseConverter.ToSimplified(_currentDetails.Title)}",
            Width = 760,
            Height = 520,
            MinWidth = 520,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AppThemeService.GetBrush("ThemeSurfaceBrush"),
            Content = new System.Windows.Controls.Border
            {
                Padding = new Thickness(16),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        Text = description,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Foreground = AppThemeService.GetBrush("ThemeTextBrush"),
                        LineHeight = 24
                    }
                }
            }
        };
        window.ShowDialog();
    }

    private void CopyVideoUrlButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlText.Text))
        {
            StatusText.Text = "当前详情没有可复制的链接。";
            return;
        }

        try { Clipboard.SetText(UrlText.Text); } catch { return; }
        StatusText.Text = "已复制视频链接。";
    }

    private void OpenVideoPageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlText.Text))
        {
            StatusText.Text = "当前详情没有可打开的链接。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = UrlText.Text,
                UseShellExecute = true
            });
            StatusText.Text = "已打开视频页面。";
        }
        catch (Exception ex)
        {
            HandleUiActionError("details", "打开视频页面失败", ex);
        }
    }

    private async Task QueueVideosForDownloadAsync(IEnumerable<VideoSummary> videos)
    {
        var added = 0;
        foreach (var video in videos)
        {
            if (HasQueueItem(video.VideoId, 0))
            {
                continue;
            }

            _downloadQueue.Add(new DownloadQueueItem
            {
                Title = string.IsNullOrWhiteSpace(video.Title) ? video.VideoId : video.Title,
                Url = video.Url,
                Type = "mp4",
                Quality = 0,
                VideoId = video.VideoId,
                TargetPath = CreateQueueTargetPath(string.IsNullOrWhiteSpace(video.Title) ? video.VideoId : video.Title, "mp4", video.VideoId, 0),
                StageText = "等待",
                QueueStatusText = "等待中"
            });
            added++;
        }

        await TrySaveDownloadQueueAsync("queue", "保存下载队列失败");
        UpdateDownloadQueueControlUi();
        StatusText.Text = $"已加入下载队列 {added} 项。";
    }

    private VideoSource? SelectSourceByQuality(IReadOnlyList<VideoSource>? sources)
    {
        if (sources is null || sources.Count == 0) return null;
        return _settings.DefaultQuality switch
        {
            "lowest" => sources.OrderBy(s => s.Quality).First(),
            "720" => sources.OrderBy(s => Math.Abs(s.Quality - 720)).First(),
            "480" => sources.OrderBy(s => Math.Abs(s.Quality - 480)).First(),
            _ => sources.OrderByDescending(s => s.Quality).First()
        };
    }

    private VideoSource? SelectSourceForQueueItem(IReadOnlyList<VideoSource>? sources, DownloadQueueItem item)
    {
        if (sources is null || sources.Count == 0) return null;
        if (item.Quality > 0)
        {
            return sources.OrderBy(s => Math.Abs(s.Quality - item.Quality)).FirstOrDefault();
        }
        return SelectSourceByQuality(sources);
    }

    private async Task<VideoSource?> ResolveQueueItemSourceAsync(DownloadQueueItem item, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operationId = CreateOperationId("queuesource");
        var apiClient = _apiClient;
        if (apiClient is null || string.IsNullOrWhiteSpace(item.VideoId))
        {
            return null;
        }

        LogInfo("queue", $"[{operationId}] 解析队列项源: videoId={item.VideoId}, quality={item.Quality}, forceRefresh={forceRefresh}");
        var details = await GetOrLoadVideoDetailsAsync(item.VideoId, cancellationToken: cancellationToken, forceRefresh: forceRefresh);
        cancellationToken.ThrowIfCancellationRequested();
        var source = SelectSourceForQueueItem(details?.Sources, item);
        if (source is null)
        {
            LogInfo("queue", $"[{operationId}] 未找到可用源: videoId={item.VideoId}");
            return null;
        }

        item.Title = string.IsNullOrWhiteSpace(details?.Title) ? item.Title : details!.Title;
        item.Url = source.Url;
        item.Type = source.Type;
        item.Quality = source.Quality;
        _reResolveItems.Remove(item);
        LogInfo("queue", $"[{operationId}] 队列项源解析完成: videoId={item.VideoId}, selectedQuality={item.Quality}, type={item.Type}");
        return source;
    }

    private bool HasQueueItem(string videoId, int quality)
    {
        return _downloadQueue.Any(item =>
            string.Equals(item.VideoId, videoId, StringComparison.OrdinalIgnoreCase) &&
            (quality == 0 || item.Quality == 0 || item.Quality == quality));
    }

    private VideoDetailsLoadOptions GetActiveVideoDetailsLoadOptions()
    {
        return VideoDetailsLoadPolicy.ForVisibility(_settings.VideoDetailsVisibility);
    }

    private async Task<VideoDetails?> GetOrLoadVideoDetailsAsync(string videoId, VideoDetailsLoadOptions? requestedLoadOptions = null, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        var loadOptions = requestedLoadOptions ?? GetActiveVideoDetailsLoadOptions();
        var cacheKey = $"{videoId}:{(int)loadOptions}";

        if (forceRefresh)
        {
            foreach (var key in _videoDetailsCache.Keys.Where(key => key.StartsWith($"{videoId}:", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _videoDetailsCache.Remove(key);
            }

            foreach (var key in _videoDetailsInFlight.Keys.Where(key => key.StartsWith($"{videoId}:", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _videoDetailsInFlight.Remove(key);
            }
        }

        if (_videoDetailsCache.TryGetValue(cacheKey, out var cached))
        {
            LogInfoThrottled("details", $"[details-cache] 命中详情缓存: {cacheKey}", TimeSpan.FromSeconds(3));
            return cached;
        }

        var cachedSuperset = _videoDetailsCache
            .Where(pair => pair.Key.StartsWith($"{videoId}:", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault(details => (details.LoadOptions & loadOptions) == loadOptions);
        if (cachedSuperset is not null)
        {
            LogInfoThrottled("details", $"[details-cache-superset] 复用详情缓存: {videoId} / {loadOptions}", TimeSpan.FromSeconds(3));
            return cachedSuperset;
        }

        if (_videoDetailsInFlight.TryGetValue(cacheKey, out var existingTask))
        {
            LogInfoThrottled("details", $"[details-inflight] 复用详情任务: {cacheKey}", TimeSpan.FromSeconds(3));
            try { return await existingTask.WaitAsync(cancellationToken); }
            catch (Exception) when (existingTask.IsFaulted || existingTask.IsCanceled)
            {
                _videoDetailsInFlight.Remove(cacheKey);
            }
        }

        var apiClient = _apiClient;
        if (apiClient is null)
        {
            return null;
        }

        // Use CancellationToken.None so the shared task is not tied to any single caller's token
        var detailsTask = apiClient.GetDetailsAsync(videoId, loadOptions, CancellationToken.None);
        _videoDetailsInFlight[cacheKey] = detailsTask;
        try
        {
            var details = await detailsTask.WaitAsync(cancellationToken);
            if (details is not null)
            {
                _videoDetailsCache[cacheKey] = details;
            }
            return details;
        }
        catch
        {
            if (_videoDetailsInFlight.TryGetValue(cacheKey, out var inFlight) && ReferenceEquals(inFlight, detailsTask))
            {
                _videoDetailsInFlight.Remove(cacheKey);
            }
            throw;
        }
        finally
        {
            if (_videoDetailsInFlight.TryGetValue(cacheKey, out var inFlight) && ReferenceEquals(inFlight, detailsTask) && detailsTask.IsCompletedSuccessfully)
            {
                _videoDetailsInFlight.Remove(cacheKey);
            }
        }
    }

    private async Task StartSelectedQueueItemsAsync(IEnumerable<DownloadQueueItem> items)
    {
        await StartQueueDownloadAsync(items);
    }

    private static TaskCompletionSource<bool> CreateDownloadQueueChangedSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void NotifyDownloadQueueChanged()
    {
        var currentSignal = _downloadQueueChangedSignal;
        _downloadQueueChangedSignal = CreateDownloadQueueChangedSignal();
        currentSignal.TrySetResult(true);
    }

    private async Task StartQueueDownloadAsync(IEnumerable<DownloadQueueItem>? preferredItems = null)
    {
        var operationId = CreateOperationId("queue");
        if (_isDownloadingQueue)
        {
            return;
        }

        var selectedItems = preferredItems?.Where(_downloadQueue.Contains).Distinct().ToList() ?? [];
        var useSelection = selectedItems.Count > 0;
        var queueItems = useSelection ? selectedItems : _downloadQueue.ToList();
        queueItems = queueItems
            .Where(item => _downloadQueue.Contains(item) && item.QueueState != DownloadQueueState.Error && item.QueueState != DownloadQueueState.Completed)
            .ToList();
        if (queueItems.Count == 0)
        {
            StatusText.Text = "下载队列为空。";
            return;
        }

        _isPauseRequested = false;
        _isDownloadingQueue = true;
        _hasPausedQueue = false;
        _currentQueueDownloadItem = null;
        _queueRunSummaryState = QueueRunSummaryState.Running;
        _queueRunTotalCount = queueItems.Count;
        _queueRunCompletedCount = 0;
        _queueRunFailedCount = 0;
        _queueRunSelectionOnly = useSelection;
        _queueRunCurrentTitle = string.Empty;
        _queueRunCurrentProgress = 0;
        LogInfo("queue", $"[{operationId}] 开始处理下载队列，items={queueItems.Count}, selectionOnly={useSelection}");
        _downloadQueueCancellationTokenSource?.Dispose();
        _downloadQueueCancellationTokenSource = new CancellationTokenSource();
        _downloadQueueChangedSignal = CreateDownloadQueueChangedSignal();
        UpdateDownloadQueueControlUi();

        try
        {
            var maxConcurrentDownloads = Math.Clamp(_settings.MaxConcurrentDownloads, 1, 3);
            var downloadGate = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);
            var pendingItems = queueItems.ToList();
            var runningTasks = new Dictionary<DownloadQueueItem, Task<QueueItemProcessResult>>();

            while (true)
            {
                foreach (var newItem in _downloadQueue)
                {
                    if (!pendingItems.Contains(newItem) && !runningTasks.ContainsKey(newItem)
                        && newItem.QueueState != DownloadQueueState.Completed
                        && newItem.QueueState != DownloadQueueState.Error)
                    {
                        pendingItems.Add(newItem);
                    }
                }

                // Dispatch re-resolve items first (bypass concurrency limit), then normal items
                var reResolveReady = pendingItems.Where(_reResolveItems.Contains).ToList();
                foreach (var item in reResolveReady)
                {
                    if (!_isPauseRequested && !runningTasks.ContainsKey(item) && _downloadQueue.Contains(item))
                    {
                        pendingItems.Remove(item);
                        DownloadQueueList.SelectedItem = item;
                        runningTasks[item] = ProcessQueueItemAsync(item, operationId, downloadGate);
                    }
                }

                while (!_isPauseRequested && pendingItems.Count > 0 && runningTasks.Count < maxConcurrentDownloads)
                {
                    var item = pendingItems[0];
                    pendingItems.RemoveAt(0);
                    if (!_downloadQueue.Contains(item) || item.QueueState == DownloadQueueState.Completed || item.QueueState == DownloadQueueState.Error)
                    {
                        _reResolveItems.Remove(item);
                        continue;
                    }

                    DownloadQueueList.SelectedItem = item;
                    runningTasks[item] = ProcessQueueItemAsync(item, operationId, downloadGate);
                }

                if (_isPauseRequested || (pendingItems.Count == 0 && runningTasks.Count == 0))
                {
                    break;
                }

                if (runningTasks.Count == 0)
                {
                    await _downloadQueueChangedSignal.Task;
                    continue;
                }

                var queueChangedTask = _downloadQueueChangedSignal.Task;
                var completedTask = await Task.WhenAny(runningTasks.Values.Cast<Task>().Append(queueChangedTask));
                if (ReferenceEquals(completedTask, queueChangedTask))
                {
                    continue;
                }

                var finishedPair = runningTasks.First(pair => pair.Value == completedTask);
                var finishedItem = finishedPair.Key;
                runningTasks.Remove(finishedItem);
                var result = await finishedPair.Value;
                ClearQueueItemCancellationToken(finishedItem);

                switch (result.Outcome)
                {
                    case QueueItemOutcome.Completed:
                        SetQueueItemVisualState(finishedItem, DownloadQueueState.Completed, "完成", "已完成", showProgress: true, progressValue: 100);
                        _downloadQueue.Remove(finishedItem);
                        _queueRunCompletedCount++;
                        await TrySaveDownloadQueueAsync("queue", "保存下载队列失败");
                        LogInfo("queue", $"[{operationId}] 队列项完成: videoId={finishedItem.VideoId}, completed={_queueRunCompletedCount}");
                        break;
                    case QueueItemOutcome.Paused:
                        _queueRunSummaryState = QueueRunSummaryState.Paused;
                        _hasPausedQueue = true;
                        break;
                    case QueueItemOutcome.Removed:
                        LogInfo("queue", $"[{operationId}] 队列项已移除: videoId={finishedItem.VideoId}");
                        break;
                    case QueueItemOutcome.Error:
                        _queueRunFailedCount++;
                        await TrySaveDownloadQueueAsync("queue", "保存下载队列失败");
                        StatusText.Text = $"下载失败，已跳过: {finishedItem.Title}";
                        LogInfo("queue", $"[{operationId}] 队列项失败并已跳过: videoId={finishedItem.VideoId}, failed={_queueRunFailedCount}");
                        break;
                }

                UpdateQueueRuntimeSummaryUi();
            }

            if (_isPauseRequested || _hasPausedQueue)
            {
                _queueRunSummaryState = QueueRunSummaryState.Paused;
                StatusText.Text = _queueRunCompletedCount == 0 ? "下载队列已暂停。" : $"下载队列已暂停，已完成 {_queueRunCompletedCount} 项。";
                return;
            }

            _hasPausedQueue = false;
            _queueRunSummaryState = QueueRunSummaryState.Completed;
            foreach (var pendingItem in _downloadQueue.Where(i => i.QueueState == DownloadQueueState.Paused))
            {
                SetQueueItemVisualState(pendingItem, DownloadQueueState.Waiting, "等待", "等待中");
            }
            UpdateQueueRuntimeSummaryUi();
            StatusText.Text = useSelection
                ? BuildQueueCompletionStatusText("选中项")
                : BuildQueueCompletionStatusText("队列");
            LogInfo("queue", $"[{operationId}] 下载队列处理结束: completed={_queueRunCompletedCount}, failed={_queueRunFailedCount}, remaining={_downloadQueue.Count}, paused={_hasPausedQueue}");
        }
        finally
        {
            foreach (var cts in _activeQueueItemCancellationTokenSources.Values) cts.Dispose();
            foreach (var cts in _activeQueueItemLinkedCancellationTokenSources.Values) cts.Dispose();
            _activeQueueItemCancellationTokenSources.Clear();
            _activeQueueItemLinkedCancellationTokenSources.Clear();
            _reResolveItems.Clear();
            _currentQueueDownloadItem = null;
            _isDownloadingQueue = false;
            _isPauseRequested = false;
            _downloadQueueCancellationTokenSource?.Dispose();
            _downloadQueueCancellationTokenSource = null;
            if (_queueRunSummaryState == QueueRunSummaryState.Completed && _downloadQueue.Count == 0)
            {
                ResetQueueRunSummaryState();
            }
            UpdateDownloadQueueControlUi();
        }
    }

    private async Task<QueueItemProcessResult> ProcessQueueItemAsync(DownloadQueueItem item, string operationId, SemaphoreSlim downloadGate)
    {
        _currentQueueDownloadItem = item;
        item.IsDownloading = true;
        item.HasError = false;
        SetQueueItemVisualState(item, DownloadQueueState.Resolving, "解析", "解析中", showProgress: true, isProgressIndeterminate: true);
        UpdateQueueRuntimeSummaryUi();
        LogInfo("queue", $"[{operationId}] 开始处理队列项: videoId={item.VideoId}, title={item.Title}, requestedQuality={item.Quality}");

        try
        {
            var queueCancellationToken = CreateQueueItemCancellationToken(item);
            VideoSource? source;
            try
            {
                source = await ResolveQueueItemSourceAsync(item, queueCancellationToken, forceRefresh: _reResolveItems.Contains(item));
            }
            catch (OperationCanceledException)
            {
                SetQueueItemVisualState(item, DownloadQueueState.Paused, "暂停", "已暂停", showProgress: true, progressValue: item.ProgressValue);
                return QueueItemProcessResult.Paused();
            }
            catch (Exception ex) when (IsCloudflareSessionError(ex))
            {
                SetQueueItemVisualState(item, DownloadQueueState.Verifying, "重验", "等待重验", showProgress: true, isProgressIndeterminate: true);
                UpdateQueueRuntimeSummaryUi();
                var verified = await EnsureVerifiedSessionAsync("下载前遇到 Cloudflare 验证，正在打开验证窗口...", queueCancellationToken);
                if (!verified)
                {
                    SetQueueItemVisualState(item, DownloadQueueState.Error, "失败", "验证失败");
                    return QueueItemProcessResult.Error();
                }

                source = await ResolveQueueItemSourceAsync(item, queueCancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                item.HasError = true;
                SetQueueItemVisualState(item, DownloadQueueState.Error, "失败", "解析失败");
                return QueueItemProcessResult.Error();
            }

            if (!_downloadQueue.Contains(item))
            {
                return QueueItemProcessResult.Removed();
            }

            if (source is null)
            {
                item.HasError = true;
                SetQueueItemVisualState(item, DownloadQueueState.Error, "失败", "无可用链接");
                return QueueItemProcessResult.Error();
            }

            await downloadGate.WaitAsync(queueCancellationToken);
            try
            {
            SetQueueItemVisualState(item, DownloadQueueState.Downloading, "下载", "下载中", showProgress: true, progressValue: 0);
            UpdateQueueRuntimeSummaryUi();
            LogInfo("queue", $"[{operationId}] 开始下载队列项: videoId={item.VideoId}, quality={item.Quality}, type={item.Type}");
            await TrySaveDownloadQueueAsync("queue", "保存下载队列失败");
            var downloaded = await DownloadSourceAsync(source, item.Title, queueCancellationToken, item);
            if (!_downloadQueue.Contains(item))
            {
                DeleteQueueItemTemporaryFile(item);
                return QueueItemProcessResult.Removed();
            }

            if (!downloaded)
            {
                if (queueCancellationToken.IsCancellationRequested)
                {
                    SetQueueItemVisualState(item, DownloadQueueState.Paused, "暂停", "已暂停", showProgress: true, progressValue: item.ProgressValue);
                    return QueueItemProcessResult.Paused();
                }

                SetQueueItemVisualState(item, DownloadQueueState.Error, "失败", item.QueueStatusText == "下载失败" ? "下载失败" : item.QueueStatusText);
                LogInfo("queue", $"[{operationId}] 队列项下载失败: videoId={item.VideoId}");
                return QueueItemProcessResult.Error();
            }

            return QueueItemProcessResult.Completed();
            }
            finally
            {
                downloadGate.Release();
            }
        }
        finally
        {
            item.IsDownloading = false;
            if (ReferenceEquals(_currentQueueDownloadItem, item))
            {
                _currentQueueDownloadItem = null;
            }
            UpdateQueueRuntimeSummaryUi();
        }
    }

    private void DeleteQueueItemTemporaryFile(DownloadQueueItem item)
    {
        if (string.IsNullOrWhiteSpace(item.TargetPath))
        {
            return;
        }

        try
        {
            var directory = EnsureDownloadDirectory();
            var targetPath = DownloadPathGuard.EnsureWithinDirectory(directory, item.TargetPath);
            var temporaryPath = targetPath + ".tmp";
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            LogInfo("download", $"忽略队列临时文件清理: {ex.Message}");
        }
    }

    private async void RemoveSelectedQueueItems(IEnumerable<DownloadQueueItem> items)
    {
        try
        {
            var selectedItems = items.ToList();
            if (selectedItems.Count == 0)
            {
                StatusText.Text = "请先选择要移除的队列项。";
                return;
            }

            var removedCurrentItem = _currentQueueDownloadItem is not null && selectedItems.Contains(_currentQueueDownloadItem);
            foreach (var item in selectedItems)
            {
                if (item.IsDownloading)
                {
                    CancelQueueItem(item);
                }
                DeleteQueueItemTemporaryFile(item);
                _downloadQueue.Remove(item);
                ClearQueueItemCancellationToken(item);
            }
            NotifyDownloadQueueChanged();

            if (removedCurrentItem)
            {
                LogInfo("queue", $"当前下载项已被用户移除: videoId={_currentQueueDownloadItem?.VideoId}");
            }

            if (!_isDownloadingQueue && _downloadQueue.Count == 0)
            {
                ResetQueueRunSummaryState();
            }

            if (!await TrySaveDownloadQueueAsync("queue", "保存下载队列失败"))
            {
                UpdateDownloadQueueControlUi();
                StatusText.Text = removedCurrentItem
                    ? $"已移除 {selectedItems.Count} 个下载队列项，当前下载会自动停止，但队列保存失败。"
                    : $"已移除 {selectedItems.Count} 个下载队列项，但队列保存失败。";
                return;
            }

            UpdateDownloadQueueControlUi();
            StatusText.Text = removedCurrentItem
                ? $"已移除 {selectedItems.Count} 个下载队列项，当前下载会自动停止并继续后续任务。"
                : $"已移除 {selectedItems.Count} 个下载队列项。";
        }
        catch (Exception ex)
        {
            HandleUiActionError("queue", "移除下载队列项失败", ex);
        }
    }

    private async Task MoveQueueItemsAsync(IEnumerable<DownloadQueueItem> items, Func<List<int>, List<int>> reorder, string successMessage)
    {
        var selectedItems = items.Where(_downloadQueue.Contains).Distinct().ToList();
        if (selectedItems.Count == 0)
        {
            StatusText.Text = "请先选择要调整顺序的队列项。";
            return;
        }

        var indexes = selectedItems.Select(item => _downloadQueue.IndexOf(item)).Where(index => index >= 0).OrderBy(index => index).ToList();
        if (indexes.Count == 0)
        {
            return;
        }

        var targetIndexes = reorder(indexes);
        if (targetIndexes.Count != indexes.Count || targetIndexes.SequenceEqual(indexes))
        {
            return;
        }

        var movingItems = indexes.Select(index => _downloadQueue[index]).ToList();
        for (var i = indexes.Count - 1; i >= 0; i--)
        {
            _downloadQueue.RemoveAt(indexes[i]);
        }

        for (var i = 0; i < movingItems.Count; i++)
        {
            _downloadQueue.Insert(targetIndexes[i], movingItems[i]);
        }

        if (!await TrySaveDownloadQueueAsync("queue", "保存下载队列失败"))
        {
            StatusText.Text = $"{successMessage}，但队列保存失败。";
            return;
        }

        DownloadQueueList.SelectedItems.Clear();
        foreach (var item in movingItems)
        {
            DownloadQueueList.SelectedItems.Add(item);
        }
        DownloadQueueList.ScrollIntoView(movingItems.First());
        StatusText.Text = successMessage;
    }

    private CancellationToken CreateQueueItemCancellationToken(DownloadQueueItem item)
    {
        if (!_activeQueueItemCancellationTokenSources.TryGetValue(item, out var itemCancellationTokenSource))
        {
            itemCancellationTokenSource = new CancellationTokenSource();
            _activeQueueItemCancellationTokenSources[item] = itemCancellationTokenSource;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _downloadQueueCancellationTokenSource?.Token ?? CancellationToken.None,
            itemCancellationTokenSource.Token);
        _activeQueueItemLinkedCancellationTokenSources[item] = linked;
        return linked.Token;
    }

    private void CancelQueueItem(DownloadQueueItem item)
    {
        if (_activeQueueItemCancellationTokenSources.TryGetValue(item, out var cancellationTokenSource))
        {
            cancellationTokenSource.Cancel();
        }
    }

    private void ClearQueueItemCancellationToken(DownloadQueueItem item)
    {
        if (_activeQueueItemCancellationTokenSources.Remove(item, out var cts))
            cts.Dispose();
        if (_activeQueueItemLinkedCancellationTokenSources.Remove(item, out var linked))
            linked.Dispose();
    }

    private void SetQueueItemVisualState(
        DownloadQueueItem item,
        DownloadQueueState state,
        string stageText,
        string statusText,
        bool showProgress = false,
        bool isProgressIndeterminate = false,
        double? progressValue = null)
    {
        item.QueueState = state;
        item.HasError = state == DownloadQueueState.Error;
        item.StageText = stageText;
        item.QueueStatusText = statusText;
        item.ShowProgress = showProgress;
        item.IsProgressIndeterminate = isProgressIndeterminate;
        item.ProgressValue = progressValue;
    }

    private void ResetQueueRunSummaryState()
    {
        _queueRunSummaryState = QueueRunSummaryState.Idle;
        _queueRunTotalCount = 0;
        _queueRunCompletedCount = 0;
        _queueRunFailedCount = 0;
        _queueRunSelectionOnly = false;
        _queueRunCurrentTitle = string.Empty;
        _queueRunCurrentProgress = 0;
    }

    private void UpdateQueueRuntimeSummaryUi()
    {
        var queuedCount = _downloadQueue.Count;
        QueueCountText.Text = queuedCount == 0 ? "队列" : $"队列 {queuedCount}";

        if (_queueRunSummaryState == QueueRunSummaryState.Idle || _queueRunTotalCount <= 0)
        {
            QueueSummaryText.Text = queuedCount == 0 ? "待处理 0 项" : $"待处理 {queuedCount} 项";
            QueueCurrentTitleText.Text = string.Empty;
            QueueCurrentTitleText.Visibility = Visibility.Collapsed;
            QueueOverallProgressBar.Visibility = Visibility.Collapsed;
            QueueOverallProgressBar.IsIndeterminate = false;
            QueueOverallProgressBar.Value = 0;
            return;
        }

        var activeItems = _downloadQueue.Where(item => item.IsDownloading).ToList();
        var activeProgressTotal = activeItems.Sum(item => Math.Max(0, Math.Min(100, item.ProgressValue ?? 0)) / 100d);
        var overallProgress = (_queueRunCompletedCount + activeProgressTotal) / Math.Max(1, _queueRunTotalCount) * 100d;
        QueueOverallProgressBar.Visibility = Visibility.Visible;
        QueueOverallProgressBar.IsIndeterminate = _queueRunSummaryState == QueueRunSummaryState.Running && activeItems.Any(item => item.ShowProgress && item.IsProgressIndeterminate);
        QueueOverallProgressBar.Value = Math.Max(0, Math.Min(100, overallProgress));

        var activeCount = activeItems.Count;
        var processedCount = Math.Min(_queueRunTotalCount, _queueRunCompletedCount + activeCount);
        var selectionPrefix = _queueRunSelectionOnly ? "选中项" : "队列";
        QueueSummaryText.Text = _queueRunSummaryState switch
        {
            QueueRunSummaryState.Running
                => $"{selectionPrefix}总进度 {_queueRunCompletedCount}/{_queueRunTotalCount}，活动 {activeCount} 项 ({overallProgress:0.#}%)",
            QueueRunSummaryState.Paused
                => $"已暂停，已完成 {_queueRunCompletedCount}/{_queueRunTotalCount} ({overallProgress:0.#}%)",
            QueueRunSummaryState.Stopped
                => _queueRunFailedCount > 0
                    ? $"已停止，已完成 {_queueRunCompletedCount}/{_queueRunTotalCount}，失败 {_queueRunFailedCount} 项"
                    : $"已停止，已完成 {_queueRunCompletedCount}/{_queueRunTotalCount}",
            QueueRunSummaryState.Completed
                => _queueRunFailedCount > 0
                    ? $"已完成 {_queueRunCompletedCount}/{_queueRunTotalCount}，失败 {_queueRunFailedCount} 项"
                    : $"已完成 {_queueRunCompletedCount}/{_queueRunTotalCount} (100%)",
            _
                => queuedCount == 0 ? "待处理 0 项" : $"待处理 {queuedCount} 项"
        };

        if (_queueRunSummaryState == QueueRunSummaryState.Running && activeCount > 0)
        {
            var activeTitles = string.Join("、", activeItems.Select(item => item.Title).Take(3));
            if (activeItems.Count > 3)
            {
                activeTitles += $" 等 {activeItems.Count} 项";
            }
            QueueCurrentTitleText.Text = $"当前 {processedCount}/{_queueRunTotalCount}: {activeTitles}";
            QueueCurrentTitleText.Visibility = Visibility.Visible;
        }
        else
        {
            QueueCurrentTitleText.Text = string.Empty;
            QueueCurrentTitleText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateDownloadQueueControlUi()
    {
        DownloadQueueButton.Content = _isDownloadingQueue ? "暂停下载" : (_hasPausedQueue ? "继续下载" : "开始下载");
        DownloadQueueButton.Style = (Style)FindResource(_isDownloadingQueue ? "SecondaryActionButtonStyle" : "PrimaryActionButtonStyle");
        DownloadQueueButton.IsEnabled = _isDownloadingQueue || _downloadQueue.Count > 0;
        RetryFailedQueueItemsButton.IsEnabled = _downloadQueue.Any(item => item.QueueState == DownloadQueueState.Error);
        ClearQueueButton.IsEnabled = !_isDownloadingQueue;
        UpdateQueueRuntimeSummaryUi();
    }

    private async Task PlayVideoSummaryAsync(VideoSummary summary)
    {
        try
        {
            if (_apiClient is null)
            {
                InitSessionWithoutCf();
            }

            var details = await GetOrLoadVideoDetailsAsync(summary.VideoId, GetActiveVideoDetailsLoadOptions());
            if (details is null)
            {
                StatusText.Text = "读取详情失败。";
                return;
            }

            var source = SelectSourceByQuality(details.Sources);
            if (source is null)
            {
                StatusText.Text = "当前详情没有可用视频源。";
                return;
            }

            _currentDetails = details;
            _currentDetailsVideoId = details.VideoId;

            var title = details.Title;
            StatusText.Text = $"正在打开播放窗口: {title} ({source.QualityText})";
            var player = new Views.PlayerWindow(_settings) { Owner = this };
            await player.OpenAsync(title, source.Url, source.Type);

            try
            {
                SaveSettings();
            }
            catch (Exception ex)
            {
                LogError("player", "保存播放器窗口设置失败", ex);
                StatusText.Text = $"正在播放: {title} ({source.QualityText})，但播放器设置保存失败。";
                return;
            }

            StatusText.Text = $"正在播放: {title} ({source.QualityText})";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"播放失败: {ex.Message}";
        }
    }

    private void OpenHistoryItemFolder(DownloadHistoryItem item)
    {
        var fullPath = item.FullPath;
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            StatusText.Text = "历史记录中没有可用的文件路径。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{fullPath}\"",
                UseShellExecute = true
            });
            StatusText.Text = $"已打开所在目录: {Path.GetFileName(fullPath)}";
        }
        catch (Exception ex)
        {
            HandleUiActionError("history", "打开所在目录失败", ex);
        }
    }

    private async Task LoadHistoryItemDetailsAsync(DownloadHistoryItem historyItem)
    {
        var summary = ParseSummaryFromUrl(historyItem.Url);
        if (summary is null)
        {
            StatusText.Text = "该历史记录无法定位视频详情，可能是旧记录仅保存了直链。";
            return;
        }

        ResultsList.SelectedItem = null;
        FavoritesList.SelectedItem = null;
        RelatedList.SelectedItem = null;
        await LoadDetailsAsync(summary);
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

        var match = VideoLinkRegex.Match(url);
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

    private void AddVideosToFavoriteFolder(IEnumerable<VideoSummary> videos, string folderName)
    {
        if (!_favoriteFolders.TryGetValue(folderName, out var favorites))
        {
            favorites = [];
            _favoriteFolders[folderName] = favorites;
            RefreshFavoriteFolders(folderName);
        }

        var addedCount = 0;
        foreach (var video in videos)
        {
            if (favorites.Any(item => item.VideoId == video.VideoId)) continue;
            favorites.Add(video);
            addedCount++;
        }

        if (!TrySaveFavorites("favorites", "保存收藏夹失败"))
        {
            RefreshFavoritesView();
            StatusText.Text = addedCount == 0 ? "所选视频已在收藏夹中，但收藏夹保存失败。" : $"已加入收藏夹 {addedCount} 项，但收藏夹保存失败。";
            return;
        }

        RefreshFavoritesView();
        StatusText.Text = addedCount == 0 ? "所选视频已在收藏夹中。" : $"已加入收藏夹 {addedCount} 项。";
    }

    private void AddVideosToFavorites(IEnumerable<VideoSummary> videos)
    {
        var folderName = FavoritesFolderBox.SelectedItem as string ?? DefaultFavoritesFolder;
        if (!_favoriteFolders.TryGetValue(folderName, out var favorites))
        {
            favorites = [];
            _favoriteFolders[folderName] = favorites;
            RefreshFavoriteFolders(folderName);
        }

        var addedCount = 0;
        foreach (var video in videos)
        {
            if (favorites.Any(item => item.VideoId == video.VideoId))
            {
                continue;
            }

            favorites.Add(video);
            addedCount++;
        }

        if (!TrySaveFavorites("favorites", "保存收藏夹失败"))
        {
            RefreshFavoritesView();
            StatusText.Text = addedCount == 0 ? "所选视频已在收藏夹中，但收藏夹保存失败。" : $"已加入收藏夹 {addedCount} 项，但收藏夹保存失败。";
            return;
        }

        RefreshFavoritesView();
        StatusText.Text = addedCount == 0 ? "所选视频已在收藏夹中。" : $"已加入收藏夹 {addedCount} 项。";
    }

    private void RemoveSelectedFavorites(IEnumerable<VideoSummary> videos)
    {
        var folderName = FavoritesFolderBox.SelectedItem as string ?? DefaultFavoritesFolder;
        if (!_favoriteFolders.TryGetValue(folderName, out var favorites))
        {
            return;
        }

        var removedIds = videos.Select(video => video.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedItems = favorites.Where(item => removedIds.Contains(item.VideoId)).ToList();
        foreach (var item in removedItems)
        {
            favorites.Remove(item);
        }

        if (!TrySaveFavorites("favorites", "保存收藏夹失败"))
        {
            RefreshFavoritesView();
            StatusText.Text = removedItems.Count == 0 ? "没有可移除的收藏。" : $"已移除 {removedItems.Count} 项收藏，但收藏夹保存失败。";
            return;
        }

        RefreshFavoritesView();
        StatusText.Text = removedItems.Count == 0 ? "没有可移除的收藏。" : $"已移除 {removedItems.Count} 项收藏。";
    }

    private async void PlaySourceInline_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDetails || sender is not FrameworkElement { Tag: VideoSource source })
        {
            return;
        }

        try
        {
            var title = _currentDetails?.Title ?? GetSelectedVideoSummary()?.Title ?? source.QualityText;
            StatusText.Text = $"正在打开播放窗口: {title} ({source.QualityText})";
            var player = new Views.PlayerWindow(_settings) { Owner = this };
            await player.OpenAsync(title, source.Url, source.Type);

            try
            {
                SaveSettings();
            }
            catch (Exception ex)
            {
                LogError("player", "保存播放器窗口设置失败", ex);
                StatusText.Text = $"正在播放: {title} ({source.QualityText})，但播放器设置保存失败。";
                return;
            }

            StatusText.Text = $"正在播放: {title} ({source.QualityText})";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"播放失败: {ex.Message}";
        }
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

    private void SourcesList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = SourcesList.SelectedItem is VideoSource;
        QueueSourceButton.IsEnabled = hasSelection && !_isLoadingDetails;
        DownloadButton.IsEnabled = hasSelection && !_isLoadingDetails;
    }

    private void SourcesList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_isLoadingDetails || SourcesList.SelectedItem is not VideoSource)
        {
            return;
        }

        QueueSourceButton_OnClick(sender, new RoutedEventArgs());
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

    private string CreateQueueTargetPath(string? title, string? type, string? videoId, int quality)
    {
        var extension = !string.IsNullOrWhiteSpace(type) && type.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ? ".m3u8" : ".mp4";
        var downloadDirectory = EnsureDownloadDirectory();
        var fileName = BuildSuggestedFileName(title, extension, videoId, quality);
        return GetDownloadTargetPath(downloadDirectory, fileName);
    }

    private static string GetDownloadTargetPath(string directory, string fileName)
    {
        var candidatePath = Path.Combine(directory, fileName);
        return DownloadPathGuard.EnsureWithinDirectory(directory, candidatePath);
    }

    private string EnsureDownloadDirectory()
    {
        var directory = string.IsNullOrWhiteSpace(_settings.DownloadPath)
            ? Path.Combine(AppContext.BaseDirectory, "downloads")
            : _settings.DownloadPath;
        return DownloadPathGuard.NormalizeDirectory(directory);
    }

    private string BuildSuggestedFileName(string? title, string extension, string? videoId = null, int quality = 0)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeTitle = string.IsNullOrWhiteSpace(title) ? $"hanime_{timestamp}" : SanitizeFileName(title);
        var safeVideoId = string.IsNullOrWhiteSpace(videoId) ? "unknown" : SanitizeFileName(videoId);
        var qualityText = quality > 0 ? $"{quality}p" : "unknown";
        var template = string.IsNullOrWhiteSpace(_settings.FileNamingRule) ? "{title}" : _settings.FileNamingRule;
        var baseName = template
            .Replace("{title}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{timestamp}", timestamp, StringComparison.OrdinalIgnoreCase)
            .Replace("{videoId}", safeVideoId, StringComparison.OrdinalIgnoreCase)
            .Replace("{quality}", qualityText, StringComparison.OrdinalIgnoreCase);

        baseName = SanitizeFileName(baseName);
        return $"{baseName}{extension}";
    }

    private static string SanitizeFileName(string title)
    {
        return DownloadPathGuard.SanitizeFileName(title, $"hanime_{DateTime.Now:yyyyMMdd_HHmmss}");
    }

    private sealed class FavoriteVideoRecord
    {
        public required string VideoId { get; init; }
        public required string Title { get; init; }
        public required string Url { get; init; }
        public string CoverUrl { get; init; } = string.Empty;
    }

    private sealed class DownloadQueueRecord
    {
        public required string Title { get; init; }
        public string Url { get; init; } = string.Empty;
        public string Type { get; init; } = "mp4";
        public int Quality { get; init; }
        public required string VideoId { get; init; }
        public string TargetPath { get; init; } = string.Empty;
    }

    private sealed class QueueItemProcessResult
    {
        public required QueueItemOutcome Outcome { get; init; }

        public static QueueItemProcessResult Completed() => new() { Outcome = QueueItemOutcome.Completed };
        public static QueueItemProcessResult Paused() => new() { Outcome = QueueItemOutcome.Paused };
        public static QueueItemProcessResult Error() => new() { Outcome = QueueItemOutcome.Error };
        public static QueueItemProcessResult Removed() => new() { Outcome = QueueItemOutcome.Removed };
    }

    private string BuildQueueCompletionStatusText(string selectionPrefix)
    {
        if (_queueRunFailedCount > 0 && _queueRunCompletedCount > 0)
        {
            return $"{selectionPrefix}下载完成，成功 {_queueRunCompletedCount} 项，失败 {_queueRunFailedCount} 项。";
        }

        if (_queueRunFailedCount > 0)
        {
            return $"{selectionPrefix}下载结束，失败 {_queueRunFailedCount} 项，可使用重新解析失败项继续。";
        }

        return $"{selectionPrefix}下载完成，共完成 {_queueRunCompletedCount} 项。";
    }

    private sealed class DownloadHistoryItem
    {
        public required string TimeText { get; init; }
        public required string FileName { get; init; }
        public required string Url { get; init; }
        public required string FullPath { get; init; }
        public required string RawText { get; init; }
        public bool FileExists => !string.IsNullOrWhiteSpace(FullPath) && File.Exists(FullPath);
        public string FileStateText => FileExists ? "文件存在" : "文件缺失";

        public static DownloadHistoryItem Create(DateTime time, string fileName, string url, string fullPath)
        {
            var rawText = $"{time:yyyy-MM-dd HH:mm:ss} | {fileName} | {url} | {fullPath}";
            return new DownloadHistoryItem
            {
                TimeText = time.ToString("yyyy-MM-dd HH:mm:ss"),
                FileName = fileName,
                Url = url,
                FullPath = fullPath,
                RawText = rawText
            };
        }

        public static DownloadHistoryItem FromRawText(string rawText)
        {
            var parts = rawText.Split(" | ", 4, StringSplitOptions.None);
            return new DownloadHistoryItem
            {
                TimeText = parts.ElementAtOrDefault(0) ?? string.Empty,
                FileName = parts.ElementAtOrDefault(1) ?? rawText,
                Url = parts.ElementAtOrDefault(2) ?? string.Empty,
                FullPath = parts.ElementAtOrDefault(3) ?? string.Empty,
                RawText = rawText
            };
        }
    }

    private enum DownloadQueueState
    {
        Waiting,
        Resolving,
        Checking,
        Verifying,
        Downloading,
        Finalizing,
        Paused,
        Error,
        Completed
    }

    private enum QueueRunSummaryState
    {
        Idle,
        Running,
        Paused,
        Stopped,
        Completed
    }

    private enum QueueItemOutcome
    {
        Completed,
        Paused,
        Error,
        Removed
    }

    private sealed class DownloadQueueItem : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private string _url = string.Empty;
        private string _type = string.Empty;
        private int _quality;
        private string _videoId = string.Empty;
        private string _targetPath = string.Empty;
        private bool _isDownloading;
        private bool _hasError;
        private DownloadQueueState _queueState = DownloadQueueState.Waiting;
        private string _queueStatusText = "等待中";
        private string _stageText = "等待";
        private double? _progressValue;
        private bool _isProgressIndeterminate;
        private bool _showProgress;

        public event PropertyChangedEventHandler? PropertyChanged;

        public required string Title
        {
            get => _title;
            set => SetField(ref _title, value);
        }

        public required string Url
        {
            get => _url;
            set => SetField(ref _url, value);
        }

        public required string Type
        {
            get => _type;
            set
            {
                if (SetField(ref _type, value))
                {
                    OnPropertyChanged(nameof(TypeText));
                }
            }
        }

        public int Quality
        {
            get => _quality;
            set
            {
                if (SetField(ref _quality, value))
                {
                    OnPropertyChanged(nameof(QualityText));
                }
            }
        }

        public string VideoId
        {
            get => _videoId;
            set => SetField(ref _videoId, value);
        }

        public string TargetPath
        {
            get => _targetPath;
            set => SetField(ref _targetPath, value);
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            set => SetField(ref _isDownloading, value);
        }

        public bool HasError
        {
            get => _hasError;
            set => SetField(ref _hasError, value);
        }

        public DownloadQueueState QueueState
        {
            get => _queueState;
            set => SetField(ref _queueState, value);
        }

        public string QueueStatusText
        {
            get => _queueStatusText;
            set => SetField(ref _queueStatusText, value);
        }

        public string StageText
        {
            get => _stageText;
            set => SetField(ref _stageText, value);
        }

        public double? ProgressValue
        {
            get => _progressValue;
            set => SetField(ref _progressValue, value);
        }

        public bool IsProgressIndeterminate
        {
            get => _isProgressIndeterminate;
            set => SetField(ref _isProgressIndeterminate, value);
        }

        public bool ShowProgress
        {
            get => _showProgress;
            set => SetField(ref _showProgress, value);
        }

        public string QualityText => Quality > 0 ? $"{Quality}p" : "未知清晰度";
        public string TypeText => Type.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ? "M3U8" : "MP4";

        public override string ToString()
        {
            return $"[{QualityText}] {Title}";
        }

        private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged(string? propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
