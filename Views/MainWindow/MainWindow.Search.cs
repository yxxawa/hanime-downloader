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

    /// <summary>按当前站点 + 自定义站点动态构建 watch 链接正则（修复：自定义站点粘贴链接失效）。</summary>
    private Regex BuildVideoLinkRegex()
    {
        var hosts = new List<string> { _settings.SiteHost };
        hosts.AddRange(_settings.CustomSiteHosts.Where(host => !string.IsNullOrWhiteSpace(host)));
        var hostKey = string.Join("|", hosts);
        if (_cachedVideoLinkRegex is null || !string.Equals(_cachedVideoLinkRegexHost, hostKey, StringComparison.OrdinalIgnoreCase))
        {
            var pattern = @"https?://(?:" + string.Join("|", hosts.Select(Regex.Escape)) + @")/watch\?v=(\d+)";
            _cachedVideoLinkRegex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            _cachedVideoLinkRegexHost = hostKey;
        }
        return _cachedVideoLinkRegex;
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
            var directVideoMatch = BuildVideoLinkRegex().Match(keyword);
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
        CloseSearchHistoryPopup();
        SearchButton_OnClick(sender, new RoutedEventArgs());
    }

    private void AddSearchHistory(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return;
        }

        _settings.SearchHistory.RemoveAll(item => string.Equals(item, keyword, StringComparison.OrdinalIgnoreCase));
        _settings.SearchHistory.Insert(0, keyword);
        if (_settings.SearchHistory.Count > 20)
        {
            _settings.SearchHistory = _settings.SearchHistory.Take(20).ToList();
        }
        SaveSettings();
    }

    private void SearchBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ShowSearchHistoryPopup();
    }

    private void SearchBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 延迟关闭，让点击历史项先于失焦处理。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (SearchHistoryPopup.IsOpen && !SearchHistoryList.IsKeyboardFocusWithin && !SearchBox.IsKeyboardFocusWithin)
            {
                CloseSearchHistoryPopup();
            }
        }));
    }

    private void ShowSearchHistoryPopup()
    {
        if (_settings.SearchHistory.Count == 0 || string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            CloseSearchHistoryPopup();
            return;
        }

        SearchHistoryList.ItemsSource = _settings.SearchHistory.ToList();
        SearchHistoryPopup.PlacementTarget = SearchBox;
        SearchHistoryPopup.IsOpen = true;
    }

    private void CloseSearchHistoryPopup()
    {
        SearchHistoryPopup.IsOpen = false;
    }

    private void SearchHistoryItem_OnMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SearchHistoryList.SelectedItem is string keyword)
        {
            SearchBox.Text = keyword;
            CloseSearchHistoryPopup();
            SearchButton_OnClick(sender, new RoutedEventArgs());
        }
    }

    private void SearchHistoryItem_OnMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SearchHistoryList.SelectedItem is string keyword)
        {
            _settings.SearchHistory.Remove(keyword);
            SaveSettings();
            SearchHistoryList.ItemsSource = _settings.SearchHistory.ToList();
        }
    }

    private string GetSearchKeyword()
    {
        return SearchBox.Text?.Trim() ?? string.Empty;
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

    private async Task SearchAsync(int page, bool isRetry = false)
    {
        if (_apiClient is null) InitSessionWithoutCf();

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
            // 一次性替换集合，避免逐条 Add 触发 N 次 CollectionChanged。
            _searchResults = new ObservableCollection<VideoSummary>(searchPage.Results);
            ApplyDownloadedFlags(_searchResults);
            ResultsList.ItemsSource = _searchResults;
            PrimeThumbnails(_searchResults);

            _currentPage = searchPage.CurrentPage;
            _totalPages = searchPage.TotalPages;
            UpdatePageNavigationUi();
            LeftTabControl.SelectedIndex = 0;
            AddSearchHistory(_currentSearchKeyword);
            StatusText.Text = _searchFilters.HasActiveFilters
                ? $"搜索完成，第 {_currentPage} 页，共 {searchPage.Results.Count} 条结果，已应用筛选。"
                : $"搜索完成，第 {_currentPage} 页，共 {searchPage.Results.Count} 条结果。";
            LogInfo("search", $"搜索完成: {_currentSearchKeyword} / 第 {_currentPage} 页 / {searchPage.Results.Count} 条");
        }
        catch (Exception ex) when (IsCloudflareSessionError(ex))
        {
            var restored = await EnsureVerifiedSessionAsync("检测到 Cloudflare 会话失效，正在自动打开验证窗口...");
            if (restored && !isRetry)
            {
                await SearchAsync(page, isRetry: true);
                return;
            }

            StatusText.Text = $"搜索失败: {ex.Message}";
        }
        catch (Exception ex)
        {
            _searchResults.Clear();
            // ParseSearchResult 的"未解析到结果"消息以固定前缀开头；其他异常显示真实消息（不再靠长度启发式误伤）。
            var isParseEmpty = ex is InvalidOperationException && ex.Message.StartsWith("搜索页已打开", StringComparison.OrdinalIgnoreCase);
            StatusText.Text = isParseEmpty ? "搜索失败：未解析到结果。" : $"搜索失败: {ex.Message}";
            LogError("search", "搜索失败", ex);
        }
        finally
        {
            _isSearching = false;
            SearchButton.IsEnabled = true;
            UpdatePageNavigationUi();
        }
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
}
