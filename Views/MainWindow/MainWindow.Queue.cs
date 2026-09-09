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
                StatusText.Text = "已加入下载队列，但队列保存失败。";
                return;
            }

            NotifyDownloadQueueChanged();
            UpdateDownloadQueueControlUi();
            StatusText.Text = _isDownloadingQueue
                ? "已加入下载队列。"
                : (startImmediately ? "已加入下载队列并开始下载。" : "已加入下载队列。");

            if (startImmediately && !_isDownloadingQueue)
            {
                // 只下载刚加入的这一项，不拉起整个队列。
                await StartQueueDownloadAsync(new[] { queueItem });
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
                // 失败状态持久化：重启后仍显示为失败，而不是静默变回等待。
                _downloadQueue.Add(new DownloadQueueItem
                {
                    Title = item.Title,
                    Url = item.Url,
                    Type = string.IsNullOrWhiteSpace(item.Type) ? "mp4" : item.Type,
                    Quality = item.Quality,
                    VideoId = item.VideoId,
                    TargetPath = item.TargetPath,
                    StageText = item.HasError ? "失败" : "等待",
                    QueueStatusText = item.HasError ? "失败" : "等待中",
                    QueueState = item.HasError ? DownloadQueueState.Error : DownloadQueueState.Waiting,
                    HasError = item.HasError
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
            TargetPath = item.TargetPath,
            HasError = item.HasError
        }).ToList();
        await AtomicFile.WriteAllTextAsync(DownloadQueueFilePath, JsonSerializer.Serialize(items, FavoritesJsonOptions));
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

    private async Task QueueVideosForDownloadAsync(IEnumerable<VideoSummary> videos)
    {
        var added = 0;
        var alreadyInHistory = 0;
        foreach (var video in videos)
        {
            if (HasQueueItem(video.VideoId, 0))
            {
                continue;
            }

            if (_historyVideoIds.Contains(video.VideoId))
            {
                alreadyInHistory++;
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
        StatusText.Text = alreadyInHistory > 0
            ? $"已加入下载队列 {added} 项（其中 {alreadyInHistory} 项已在下载历史中）。"
            : $"已加入下载队列 {added} 项。";
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
        // 队列只需视频源：走 download 页轻量加载（~30KB），不拉包含相关视频/简介的 watch 页（~148KB）。
        var details = await GetOrLoadVideoDetailsAsync(item.VideoId, VideoDetailsLoadOptions.Basic | VideoDetailsLoadOptions.Sources, cancellationToken, forceRefresh);
        cancellationToken.ThrowIfCancellationRequested();
        var requestedQuality = item.Quality;
        var source = SelectSourceForQueueItem(details?.Sources, item);
        if (source is null)
        {
            LogInfo("queue", $"[{operationId}] 未找到可用源: videoId={item.VideoId}");
            return null;
        }

        if (requestedQuality > 0 && source.Quality != requestedQuality)
        {
            // 请求的清晰度不存在：就近回退并明确提示，不再静默。
            LogInfo("queue", $"[{operationId}] 画质回退: videoId={item.VideoId}, {requestedQuality}p -> {source.Quality}p");
            item.QueueStatusText = $"等待中（画质回退 {requestedQuality}p→{source.Quality}p）";
        }

        item.Title = string.IsNullOrWhiteSpace(details?.Title) ? item.Title : details!.Title;
        item.Url = source.Url;
        item.Type = source.Type;
        item.Quality = source.Quality;

        // 批量入队的项入队时 Quality=0，{quality} 命名会渲染成 unknown；解析出真实画质后刷新目标路径。
        if (_settings.FileNamingRule.Contains("{quality}", StringComparison.OrdinalIgnoreCase) &&
            item.TargetPath.Contains("unknown", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var regenerated = CreateQueueTargetPath(item.Title, item.Type, item.VideoId, item.Quality);
                if (!string.Equals(regenerated, item.TargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    item.TargetPath = regenerated;
                    LogInfo("queue", $"[{operationId}] 画质已确定，更新目标路径: {item.TargetPath}");
                }
            }
            catch (Exception ex)
            {
                LogError("queue", $"重新生成目标路径失败，保持原路径: {item.TargetPath}", ex);
            }
        }

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
                if (!useSelection)
                {
                    // 全队列模式：运行中新增的队列项会被收养；选中模式（单视频下载/重新解析）不收养。
                    foreach (var newItem in _downloadQueue)
                    {
                        if (!pendingItems.Contains(newItem) && !runningTasks.ContainsKey(newItem)
                            && newItem.QueueState != DownloadQueueState.Completed
                            && newItem.QueueState != DownloadQueueState.Error)
                        {
                            pendingItems.Add(newItem);
                        }
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
            DisposeRetiredHttpClients();
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
                // 解析阶段的瞬时网络错误按重试策略重试；返回 null 仍走"无可用链接"。
                source = await CreateDownloadRetryPolicy().ExecuteAsync(
                    async (_, token) => await ResolveQueueItemSourceAsync(item, token, forceRefresh: _reResolveItems.Contains(item)),
                    queueCancellationToken);
            }
            catch (OperationCanceledException) when (!queueCancellationToken.IsCancellationRequested)
            {
                // 解析阶段超时/断流：真正的失败，而非"暂停"。
                SetQueueItemVisualState(item, DownloadQueueState.Error, "失败", "解析超时");
                return QueueItemProcessResult.Error();
            }
            catch (OperationCanceledException)
            {
                SetQueueItemVisualState(item, DownloadQueueState.Paused, "暂停", "已暂停", showProgress: true, progressValue: item.ProgressValue);
                return QueueItemProcessResult.Paused();
            }
            catch (Exception ex) when (IsCloudflareSessionError(ex))
            {
                try
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
                catch (OperationCanceledException) when (queueCancellationToken.IsCancellationRequested)
                {
                    SetQueueItemVisualState(item, DownloadQueueState.Paused, "暂停", "已暂停", showProgress: true, progressValue: item.ProgressValue);
                    return QueueItemProcessResult.Paused();
                }
                catch (Exception innerEx) when (innerEx is not OperationCanceledException)
                {
                    item.HasError = true;
                    SetQueueItemVisualState(item, DownloadQueueState.Error, "失败", "重验后解析失败");
                    return QueueItemProcessResult.Error();
                }
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

            // 暂停时等待信号量不再逃逸异常：转为 Paused 结果，队列正常运行不中断。
            try
            {
                await downloadGate.WaitAsync(queueCancellationToken);
            }
            catch (OperationCanceledException)
            {
                SetQueueItemVisualState(item, DownloadQueueState.Paused, "暂停", "已暂停", showProgress: true, progressValue: item.ProgressValue);
                return QueueItemProcessResult.Paused();
            }
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

    /// <summary>进度回调里的整表重算做节流（250ms），避免多任务时 UI 线程频繁 LINQ 与 GC。</summary>
    private void UpdateQueueRuntimeSummaryUiThrottled()
    {
        var now = Environment.TickCount64;
        if (now - _lastQueueSummaryUiTick < 250)
        {
            return;
        }

        _lastQueueSummaryUiTick = now;
        UpdateQueueRuntimeSummaryUi();
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
}
