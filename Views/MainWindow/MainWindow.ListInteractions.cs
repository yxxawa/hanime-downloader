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
}
