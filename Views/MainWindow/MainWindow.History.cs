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
            RebuildHistoryVideoIds();
            RefreshDownloadedFlags();
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

        menu.Items.Add(new Separator());
        var deleteItem = new MenuItem { Header = "删除该条" };
        deleteItem.Click += async (_, _) =>
        {
            foreach (var item in selectedHistoryItems.ToList())
            {
                _historyItems.Remove(item);
            }
            RebuildHistoryVideoIds();
            await TrySaveDownloadHistoryAsync("history", "保存下载历史失败");
            RefreshHistoryView();
            RefreshDownloadedFlags();
            StatusText.Text = $"已删除 {selectedHistoryItems.Count} 条历史记录。";
        };
        menu.Items.Add(deleteItem);

        menu.PlacementTarget = HistoryList;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void LoadDownloadHistory()
    {
        _historyItems.Clear();
        if (File.Exists(DownloadHistoryFilePath))
        {
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

        RebuildHistoryVideoIds();
        RefreshDownloadedFlags();
    }

    /// <summary>按当前历史列表重建 videoId 集合（下载完成 / 删除 / 清空历史后调用）。</summary>
    private void RebuildHistoryVideoIds()
    {
        _historyVideoIds.Clear();
        foreach (var item in _historyItems)
        {
            var match = BuildVideoLinkRegex().Match(item.Url);
            if (match.Success)
            {
                _historyVideoIds.Add(match.Groups[1].Value);
            }
        }
    }

    /// <summary>把「已下载」标记同步到已加载的搜索结果 / 收藏 / 相关视频列表。</summary>
    private void ApplyDownloadedFlags(IEnumerable<VideoSummary>? videos)
    {
        if (videos is null)
        {
            return;
        }

        foreach (var video in videos)
        {
            video.IsDownloaded = !string.IsNullOrWhiteSpace(video.VideoId) && _historyVideoIds.Contains(video.VideoId);
        }
    }

    /// <summary>下载历史变化后刷新所有列表与详情面板的「已下载」标记。</summary>
    private void RefreshDownloadedFlags()
    {
        ApplyDownloadedFlags(_searchResults);
        foreach (var folder in _favoriteFolders.Values)
        {
            ApplyDownloadedFlags(folder);
        }

        ApplyDownloadedFlags(_currentDetails?.RelatedVideos);
        UpdateCurrentDetailsDownloadedBadge();
    }

    private void UpdateCurrentDetailsDownloadedBadge()
    {
        DetailsDownloadedBadge.Visibility =
            !string.IsNullOrWhiteSpace(_currentDetailsVideoId) && _historyVideoIds.Contains(_currentDetailsVideoId)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async Task SaveDownloadHistoryAsync()
    {
        // 按视频页 URL 去重并截断到 2000 条，避免历史无限增长。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruned = _historyItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Url) && seen.Add(item.Url))
            .Take(2000)
            .ToList();
        await AtomicFile.WriteAllTextAsync(DownloadHistoryFilePath, JsonSerializer.Serialize(pruned.Select(item => item.RawText), FavoritesJsonOptions));
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

        // 文件存在性改为缓存值：避免每次绑定都做磁盘 IO（见 DownloadHistoryItem.FileExists）。
        // 刷新本身做 2 秒节流，防止连续下载完成时反复扫描整份历史。
        var now = Environment.TickCount64;
        if (now - _historyFileStateRefreshedAt >= 2000)
        {
            _historyFileStateRefreshedAt = now;
            foreach (var item in _historyItems)
            {
                item.RefreshFileState();
            }
        }
    }

    private void HistorySearchBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RestartFilterTimer(_historyFilterTimer);
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
}
