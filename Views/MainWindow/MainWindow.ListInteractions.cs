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

    private async void ResultsList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isDragSelecting) return;
        try
        {
            if (ResultsList.SelectedItem is VideoSummary summary)
            {
                FavoritesList.SelectedItem = null;
                HistoryList.SelectedItem = null;
                await LoadDetailsAsync(summary);
            }
        }
        catch (Exception ex)
        {
            HandleUiActionError("details", "读取详情失败", ex);
        }
    }

    private void ResultsList_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowVideoListContextMenu(ResultsList, e.GetPosition(ResultsList));
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

            await LoadDetailsAsync(summary);
        }
        catch (Exception ex)
        {
            HandleUiActionError("details", "读取详情失败", ex);
        }
    }

    private void ShowVideoListContextMenu(ListBox listBox, Point position, bool isFavoriteList = false)
    {
        if (GetListBoxItemAt(listBox, position)?.DataContext is not VideoSummary hit) return;
        if (!listBox.SelectedItems.Contains(hit)) listBox.SelectedItem = hit;
        var videos = listBox.SelectedItems.OfType<VideoSummary>().ToList();
        if (videos.Count == 0) return;

        // 结果和收藏的单击会加载详情；只有尚未加载的单项才需要额外入口。
        var needsDetails = ReferenceEquals(listBox, RelatedList) || (videos.Count == 1 && !_isLoadingDetails &&
            !string.Equals(_currentDetailsVideoId, videos[0].VideoId, StringComparison.OrdinalIgnoreCase));
        var menu = BuildVideoContextMenu(videos, isFavoriteList, showDetailsAction: needsDetails);
        ShowListContextMenu(menu, listBox);
    }

    private ContextMenu BuildVideoContextMenu(IReadOnlyList<VideoSummary> videos, bool isFavoriteList, bool showDetailsAction)
    {
        var menu = new ContextMenu();
        if (videos.Count == 1)
        {
            if (showDetailsAction)
                menu.Items.Add(CreateAsyncMenuAction("查看详情", "details", "读取详情失败", () => LoadDetailsAsync(videos[0])));
            menu.Items.Add(CreateAsyncMenuAction("播放视频", "player", "播放视频失败", () => PlayVideoSummaryAsync(videos[0])));
        }

        if (_isDownloadingQueue)
        {
            // 此时“立即下载”也只能入队，不再显示两个效果相同的选项。
            menu.Items.Add(CreateAsyncMenuAction("加入下载队列", "queue", "加入下载队列失败", () => QueueVideosForDownloadAsync(videos)));
        }
        else
        {
            var download = new MenuItem { Header = "下载" };
            download.Items.Add(CreateAsyncMenuAction("立即下载所选视频", "queue", "下载所选视频失败", () => QueueVideosForDownloadAsync(videos, startImmediately: true)));
            download.Items.Add(CreateAsyncMenuAction("仅加入队列", "queue", "加入下载队列失败", () => QueueVideosForDownloadAsync(videos)));
            menu.Items.Add(download);
        }

        menu.Items.Add(new Separator());
        if (isFavoriteList)
        {
            menu.Items.Add(IsAccountFavoritesMode
                ? CreateAsyncMenuAction("取消账号收藏", "favorites", "取消收藏失败", () => RemoveSelectedAccountFavoritesAsync(videos))
                : CreateMenuAction("从当前收藏夹移除", () => RemoveSelectedFavorites(videos)));
        }
        else if (IsAccountFavoritesMode)
        {
            menu.Items.Add(CreateAsyncMenuAction("添加到账号收藏", "favorites", "添加收藏失败", () => AddVideosToAccountFavoritesAsync(videos)));
        }
        else if (_favoriteFolders.Count > 1)
        {
            var favorite = new MenuItem { Header = "添加到收藏夹" };
            foreach (var folder in _favoriteFolders.Keys)
                favorite.Items.Add(CreateMenuAction(folder, () => AddVideosToFavoriteFolder(videos, folder)));
            menu.Items.Add(favorite);
        }
        else
        {
            menu.Items.Add(CreateMenuAction("添加到收藏夹", () => AddVideosToFavorites(videos)));
        }

        return menu;
    }

    private static MenuItem CreateMenuAction(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem CreateAsyncMenuAction(string header, string category, string error, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { HandleUiActionError(category, error, ex); }
        };
        return item;
    }

    private static void ShowListContextMenu(ContextMenu menu, ListBox list)
    {
        menu.PlacementTarget = list;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }
}
