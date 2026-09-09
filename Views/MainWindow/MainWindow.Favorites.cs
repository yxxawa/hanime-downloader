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
            // 确认后才从所有收藏夹移除（此前是静默全局移除，用户容易误删）。
            var result = MessageBox.Show(this, "该视频已在收藏夹中，要从所有收藏夹移除吗？", "取消收藏",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                RemoveVideoFromFavorites(summary.VideoId);
            }
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

    private async void FavoritesList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isDragSelecting) return;
        try
        {
            if (FavoritesList.SelectedItem is VideoSummary summary)
            {
                ResultsList.SelectedItem = null;
                HistoryList.SelectedItem = null;
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

    private void SaveFavorites()
    {
        var exportData = _favoriteFolders.ToDictionary(
            item => item.Key,
            item => GetFavoriteRecords(item.Value),
            StringComparer.OrdinalIgnoreCase);
        AtomicFile.WriteAllText(FavoritesFilePath, JsonSerializer.Serialize(exportData, FavoritesJsonOptions));
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

        ApplyDownloadedFlags(favorites);
        FavoritesList.ItemsSource = favorites;
        _favoritesView = CollectionViewSource.GetDefaultView(FavoritesList.ItemsSource);
        if (_favoritesView is not null)
        {
            _favoritesView.Filter = MatchesFavoriteFilter;
            _favoritesView.Refresh();
        }
        PrimeThumbnails(favorites);
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
}
