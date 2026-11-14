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

    private const VideoDetailsLoadOptions QueueDetailsOptions =
        VideoDetailsLoadOptions.Basic | VideoDetailsLoadOptions.Meta | VideoDetailsLoadOptions.Sources;

    private async void QueueVideoSource(VideoSource source, bool startImmediately = false)
    {
        try
        {
            var current = _currentDetails;
            if (current is null || string.IsNullOrWhiteSpace(current.VideoId) || !current.Sources.Contains(source))
            {
                StatusText.Text = "请先选择已加载详情的视频源。";
                return;
            }
            var videoId = current.VideoId;
            if (HasQueueItem(videoId, source.Quality))
            {
                StatusText.Text = "该视频源已经在下载队列中。";
                return;
            }

            var siteHost = _settings.SiteHost;
            var rule = _settings.FileNamingRule;
            var namingTime = DateTime.Now;
            var details = current.LoadOptions.HasFlag(VideoDetailsLoadOptions.Meta)
                ? current
                : await LoadQueueEnqueueDetailsAsync(videoId, siteHost);
            if (!CanEnqueueForSite(siteHost)) return;
            if (details is null || string.IsNullOrWhiteSpace(details.Title))
            {
                StatusText.Text = "未读取到视频详情，未加入下载队列。";
                return;
            }
            if (HasQueueItem(videoId, source.Quality)) return;
            var queueItem = CreateQueueItem(details, source, rule, namingTime);

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
                RequestQueuePause();
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
        if (GetListBoxItemAt(DownloadQueueList, e.GetPosition(DownloadQueueList))?.DataContext is not DownloadQueueItem) return;
        var items = DownloadQueueList.SelectedItems.OfType<DownloadQueueItem>().ToList();
        if (items.Count == 0) return;
        ShowListContextMenu(BuildQueueContextMenu(items), DownloadQueueList);
    }

    private ContextMenu BuildQueueContextMenu(IReadOnlyList<DownloadQueueItem> items)
    {
        var menu = new ContextMenu();
        var hasFailures = items.Any(item => item.QueueState == DownloadQueueState.Error);
        var canStart = items.Any(CanStartQueueItem);
        if (!_isDownloadingQueue)
        {
            if (canStart || hasFailures)
            {
                var header = hasFailures ? (canStart ? "下载 / 重试所选项" : "重新解析并重试")
                    : items.All(item => item.QueueState == DownloadQueueState.Paused) ? "继续所选项" : "开始所选项";
                menu.Items.Add(CreateAsyncMenuAction(header, "queue", "处理所选下载项失败", () => StartSelectedQueueItemsAsync(items)));
            }

            if (items.All(CanChangeQueueNaming))
                menu.Items.Add(CreateAsyncMenuAction("修改命名规则…", "queue", "修改命名失败", () => CustomizeQueueNamingAsync(items)));

            var indexes = items.Select(item => _downloadQueue.IndexOf(item)).Where(index => index >= 0).Order().ToList();
            var reorder = new MenuItem { Header = "调整顺序" };
            var distinctMoves = new List<List<int>>();
            void AddMove(string header, string success, Func<List<int>, List<int>> transform)
            {
                var target = transform(indexes);
                if (target.Any(index => index < 0 || index >= _downloadQueue.Count) || target.SequenceEqual(indexes) ||
                    distinctMoves.Any(move => move.SequenceEqual(target))) return;
                distinctMoves.Add(target);
                reorder.Items.Add(CreateAsyncMenuAction(header, "queue", "调整队列顺序失败",
                    () => MoveQueueItemsAsync(items, transform, success)));
            }
            AddMove("上移一位", "已上移选中项。", values => values.Select(index => index - 1).ToList());
            AddMove("移到队首", "已将选中项置顶。", values => Enumerable.Range(0, values.Count).ToList());
            AddMove("下移一位", "已下移选中项。", values => values.Select(index => index + 1).ToList());
            AddMove("移到队尾", "已将选中项置底。", values => Enumerable.Range(_downloadQueue.Count - values.Count, values.Count).ToList());
            if (reorder.Items.Count > 0) menu.Items.Add(reorder);
        }
        else if (CanRetryFailedQueueItems && hasFailures)
        {
            menu.Items.Add(CreateAsyncMenuAction("将失败项加入本轮重试", "queue", "重新解析失败",
                () => ReResolveQueueItemsAsync(items)));
        }

        // 全队列的暂停由工具栏负责；右键只影响所选项。
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var removeHeader = items.Any(item => item.IsDownloading) ? "停止并移出队列" : "移出队列";
        menu.Items.Add(CreateMenuAction(removeHeader, () => RemoveSelectedQueueItems(items)));
        return menu;
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

    private bool CanRetryFailedQueueItems => !_isDownloadingQueue || (!_queueRunSelectionOnly && !_isPauseRequested);

    private async Task ReResolveQueueItemsAsync(IReadOnlyList<DownloadQueueItem> items)
    {
        if (!CanRetryFailedQueueItems)
        {
            StatusText.Text = "请等待本轮选中项下载或暂停结束后再重试。";
            return;
        }
        var failed = items.Where(item => _downloadQueue.Contains(item) && item.QueueState == DownloadQueueState.Error).Distinct().ToList();
        if (failed.Count == 0) return;
        MarkQueueItemsForRetry(failed);

        if (_isDownloadingQueue)
        {
            NotifyDownloadQueueChanged();
            StatusText.Text = $"已将 {failed.Count} 个失败项加入本轮重试。";
            return;
        }

        await StartQueueDownloadAsync(failed);
    }

    private void MarkQueueItemsForRetry(IReadOnlyList<DownloadQueueItem> failed)
    {
        if (failed.Count == 0) return;
        foreach (var item in failed)
        {
            item.IsDownloading = false;
            SetQueueItemVisualState(item, DownloadQueueState.Waiting, "等待", "待重新解析");
            _reResolveItems.Add(item);
        }
        UpdateDownloadQueueControlUi();
        ScheduleDownloadQueueSave();
    }

    private async void ClearQueueButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MessageBox.Show(this, "确定清空下载队列吗？", "清空下载队列", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            var removedItems = _downloadQueue.ToList();
            _downloadQueue.Clear();
            foreach (var item in removedItems)
            {
                DeleteQueueItemTemporaryFile(item);
                ClearQueueItemCancellationToken(item);
            }

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
                    NamingRule = DownloadNaming.TryValidate(item.NamingRule, out _) ? item.NamingRule : null,
                    NamingAuthor = item.NamingAuthor,
                    NamingDate = item.NamingDate,
                    UploadDate = item.UploadDate,
                    NamingTime = item.NamingTime ?? DateTime.Now,
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

    private async Task QueueVideosForDownloadAsync(IEnumerable<VideoSummary> videos, bool startImmediately = false)
    {
        var selection = videos.Where(video => !string.IsNullOrWhiteSpace(video.VideoId))
            .DistinctBy(video => video.VideoId, StringComparer.OrdinalIgnoreCase).ToList();
        if (selection.Count == 0) return;

        var siteHost = _settings.SiteHost;
        var rule = _settings.FileNamingRule;
        var namingTime = DateTime.Now;
        var existingIds = _downloadQueue.Select(item => item.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = selection.Where(video => !existingIds.Contains(video.VideoId)).ToList();
        var loaded = new Dictionary<string, VideoDetails>(StringComparer.OrdinalIgnoreCase);
        if (pending.Count > 0)
        {
            StatusText.Text = $"正在读取 {pending.Count} 个所选视频的详情…";
            // 和详情面板共用详情缓存，分批获取，避免多选时同时发出大量请求。
            foreach (var batch in pending.Chunk(3))
            {
                var results = await Task.WhenAll(batch.Select(video => LoadQueueEnqueueDetailsAsync(video.VideoId, siteHost)));
                if (!CanEnqueueForSite(siteHost)) return;
                foreach (var details in results.OfType<VideoDetails>()) loaded[details.VideoId] = details;
            }
        }

        var selectedJobs = new List<DownloadQueueItem>();
        var added = 0;
        var alreadyInHistory = 0;
        var missedDetails = 0;
        foreach (var video in selection)
        {
            // 详情等待期间，另一个入口可能已经添加了同一项。沿用已选的源，不重复排队。
            var existing = _downloadQueue.Where(item => string.Equals(item.VideoId, video.VideoId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (existing.Count > 0)
            {
                if (startImmediately && existing.FirstOrDefault(CanStartQueueItem) is { } waiting) selectedJobs.Add(waiting);
                continue;
            }

            if (!loaded.TryGetValue(video.VideoId, out var details) || string.IsNullOrWhiteSpace(details.Title))
            {
                missedDetails++;
                continue;
            }
            if (_historyVideoIds.Contains(video.VideoId)) alreadyInHistory++;
            var queued = CreateQueueItem(details, SelectSourceByQuality(details.Sources), rule, namingTime);
            _downloadQueue.Add(queued);
            selectedJobs.Add(queued);
            added++;
        }

        var message = string.Empty;
        if (added > 0)
        {
            var saved = await TrySaveDownloadQueueAsync("queue", "保存下载队列失败");
            NotifyDownloadQueueChanged();
            UpdateDownloadQueueControlUi();
            message = !saved ? $"已加入下载队列 {added} 项，但保存失败。" : alreadyInHistory > 0
                ? $"已加入下载队列 {added} 项（其中 {alreadyInHistory} 项已在下载历史中）。"
                : $"已加入下载队列 {added} 项。";
        }
        if (missedDetails > 0)
            message += $"有 {missedDetails} 项未读取到完整详情，未加入队列，可重试。";
        if (message.Length == 0 && selectedJobs.Count == 0)
            message = startImmediately ? "所选视频没有可启动的队列项。失败项请在队列中重试。" : "所选视频已在下载队列中。";
        if (message.Length > 0) StatusText.Text = message;

        if (startImmediately && selectedJobs.Count > 0 && !_isDownloadingQueue && CanEnqueueForSite(siteHost))
            await StartQueueDownloadAsync(selectedJobs);
    }

    private bool CanEnqueueForSite(string siteHost) => !_isWindowClosing &&
        string.Equals(_settings.SiteHost, siteHost, StringComparison.OrdinalIgnoreCase);

    private async Task<VideoDetails?> LoadQueueEnqueueDetailsAsync(string videoId, string siteHost)
    {
        if (!CanEnqueueForSite(siteHost)) return null;
        try
        {
            if (_apiClient is null) InitSessionWithoutCf();
            try
            {
                return await GetOrLoadVideoDetailsAsync(videoId, QueueDetailsOptions);
            }
            catch (Exception ex) when (CanEnqueueForSite(siteHost) && IsCloudflareSessionError(ex))
            {
                var verified = await EnsureVerifiedSessionAsync("读取入队视频详情需要完成站点验证。", failure: ex);
                return verified && CanEnqueueForSite(siteHost) ? await GetOrLoadVideoDetailsAsync(videoId, QueueDetailsOptions) : null;
            }
        }
        catch (Exception ex)
        {
            LogError("queue", $"入队前读取详情失败: videoId={videoId}", ex);
            return null;
        }
    }

    private static string GetQueueDetailTitle(VideoDetails details) => SimplifiedChineseConverter.ToSimplified(details.Title);

    private DownloadQueueItem CreateQueueItem(VideoDetails details, VideoSource? source, string rule, DateTime namingTime)
    {
        var item = new DownloadQueueItem
        {
            Title = GetQueueDetailTitle(details),
            VideoId = details.VideoId,
            Url = source?.Url ?? details.Url,
            Type = source?.Type ?? "mp4",
            Quality = source?.Quality ?? 0,
            UploadDate = details.UploadDate,
            NamingRule = rule,
            NamingTime = namingTime,
            StageText = "等待",
            QueueStatusText = source is null ? "待解析视频源" : "等待中"
        };
        item.TargetPath = CreateQueueTargetPath(item);
        return item;
    }

    private static bool CanChangeQueueNaming(DownloadQueueItem item) => !item.IsDownloading &&
        (string.IsNullOrWhiteSpace(item.TargetPath) ||
         (!File.Exists(item.TargetPath) && !File.Exists(item.TargetPath + ".tmp") && !Directory.Exists(item.TargetPath + ".hls")));

    private async Task CustomizeQueueNamingAsync(IReadOnlyList<DownloadQueueItem> items)
    {
        if (_isDownloadingQueue || items.Count == 0 || items.Any(item => !CanChangeQueueNaming(item)))
        {
            StatusText.Text = "请先暂停队列；已有下载文件或续传数据的项目不能修改命名。";
            return;
        }
        var first = items[0];
        var dialog = new QueueNamingDialog(first.NamingRule ?? _settings.FileNamingRule,
            new DownloadNamingContext(first.Title, first.VideoId, first.Quality, first.NamingAuthor,
                first.UploadDate, first.NamingTime), items.Count, first.NamingAuthor, first.NamingDate) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (_isDownloadingQueue || items.Any(item => !_downloadQueue.Contains(item) || !CanChangeQueueNaming(item))) return;
        // 先验证所有目标路径，再更新队列；不会移动、覆盖或删除任何现有下载。
        var changes = items.Select(item => new
        {
            Item = item,
            Author = items.Count == 1 ? dialog.AuthorOverride : item.NamingAuthor,
            Date = items.Count == 1 ? dialog.DateOverride : item.NamingDate,
            Time = item.NamingTime ?? DateTime.Now
        }).Select(change => new
        {
            change.Item, change.Author, change.Date, change.Time,
            Path = CreateQueueTargetPath(change.Item.Title, change.Item.Type, change.Item.VideoId, change.Item.Quality,
                dialog.NamingRule, change.Author, change.Date ?? change.Item.UploadDate, change.Time)
        }).ToList();
        foreach (var change in changes)
        {
            change.Item.NamingRule = dialog.NamingRule;
            change.Item.NamingAuthor = change.Author;
            change.Item.NamingDate = change.Date;
            change.Item.NamingTime = change.Time;
            change.Item.TargetPath = change.Path;
        }
        var saved = await TrySaveDownloadQueueAsync("queue", "保存自定义命名失败");
        StatusText.Text = saved ? $"已为 {items.Count} 个选中视频设置命名规则，其他视频不变。" : "命名已更新，但保存队列失败。";
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

    private async Task<VideoSource?> ResolveQueueItemSourceAsync(DownloadQueueItem item, CancellationToken cancellationToken = default,
        bool forceRefresh = false, bool backgroundRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operationId = CreateOperationId("queuesource");
        var apiClient = _apiClient;
        if (apiClient is null || string.IsNullOrWhiteSpace(item.VideoId))
        {
            return null;
        }

        LogInfo("queue", $"[{operationId}] 解析队列项源: videoId={item.VideoId}, quality={item.Quality}, forceRefresh={forceRefresh}");
        // 下载命名必须使用 watch 页的完整标题（含作者前缀），不能复用列表/download 页的简略标题。
        // Meta 使加载器读取 watch 页；已加载的完整详情仍可直接复用，不必逐个打开详情面板。
        var details = await GetOrLoadVideoDetailsAsync(item.VideoId, QueueDetailsOptions, cancellationToken, forceRefresh);
        cancellationToken.ThrowIfCancellationRequested();
        // 会话恢复触发的后台解析可能比队列任务晚返回，不得改写在途任务的目标路径。
        if (backgroundRefresh && (!ReferenceEquals(apiClient, _apiClient) || item.IsDownloading || !_downloadQueue.Contains(item))) return null;
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

        if (details is not null && !string.IsNullOrWhiteSpace(details.Title)) item.Title = GetQueueDetailTitle(details);
        item.NamingRule ??= _settings.FileNamingRule;
        item.NamingTime ??= DateTime.Now;
        if (!string.IsNullOrWhiteSpace(details?.UploadDate)) item.UploadDate = details.UploadDate;
        item.Url = source.Url;
        item.Type = source.Type;
        item.Quality = source.Quality;

        // 详情就绪后再确定文件名，修正批量入队时只有列表标题/unknown 画质的临时路径。
        // 已有成品、MP4 续传文件或 HLS 分片时保留旧路径，避免重解析导致丢失下载进度。
        var hasExistingDownload = !string.IsNullOrWhiteSpace(item.TargetPath) &&
            (File.Exists(item.TargetPath) || File.Exists(item.TargetPath + ".tmp") || Directory.Exists(item.TargetPath + ".hls"));
        if (!hasExistingDownload)
        {
            var regenerated = CreateQueueTargetPath(item);
            if (!string.Equals(regenerated, item.TargetPath, StringComparison.OrdinalIgnoreCase))
            {
                item.TargetPath = regenerated;
                LogInfo("queue", $"[{operationId}] 完整详情已就绪，更新目标路径: {item.TargetPath}");
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

    private static bool CanStartQueueItem(DownloadQueueItem item) =>
        !item.IsDownloading && item.QueueState is not (DownloadQueueState.Error or DownloadQueueState.Completed);

    private async Task StartSelectedQueueItemsAsync(IReadOnlyList<DownloadQueueItem> items)
    {
        if (_isDownloadingQueue) return;
        var selected = items.Where(_downloadQueue.Contains).Distinct().ToList();
        MarkQueueItemsForRetry(selected.Where(item => item.QueueState == DownloadQueueState.Error).ToList());
        await StartQueueDownloadAsync(selected);
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

    private void RequestQueuePause()
    {
        if (_isPauseRequested) return;
        _isPauseRequested = true;
        _downloadQueueCancellationTokenSource?.Cancel();
        UpdateDownloadQueueControlUi();
        StatusText.Text = "正在暂停下载队列...";
    }

    private async Task StartQueueDownloadAsync(IEnumerable<DownloadQueueItem>? preferredItems = null)
    {
        var operationId = CreateOperationId("queue");
        if (_isDownloadingQueue)
        {
            return;
        }

        var useSelection = preferredItems is not null;
        var queueItems = (preferredItems ?? _downloadQueue).Where(item => _downloadQueue.Contains(item) && CanStartQueueItem(item)).Distinct().ToList();
        if (queueItems.Count == 0)
        {
            StatusText.Text = useSelection ? "所选项目没有可启动的下载任务。" : "下载队列没有可启动的任务。";
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
            using var downloadGate = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);
            var pendingItems = queueItems.ToList();
            var runningTasks = new Dictionary<DownloadQueueItem, Task<QueueItemProcessResult>>();

            while (true)
            {
                if (!useSelection && !_isPauseRequested)
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

                // 暂停停止派发，但必须收完所有在途结果后才能开放下一轮。
                if (runningTasks.Count == 0 && (_isPauseRequested || pendingItems.Count == 0))
                {
                    await FlushScheduledDownloadQueueSaveAsync();
                    // 全队列模式在最终落盘让出 UI 时也可能收到新项。
                    if (!useSelection && !_isPauseRequested && !_isWindowClosing &&
                        _downloadQueue.Any(item => item.QueueState is not (DownloadQueueState.Completed or DownloadQueueState.Error)))
                        continue;
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
                _activeDownloadTargets.Remove(finishedItem, out var activeTarget);
                ClearQueueItemCancellationToken(finishedItem);
                if (!_downloadQueue.Contains(finishedItem))
                {
                    DeleteQueueItemTemporaryFile(finishedItem, activeTarget);
                    result = QueueItemProcessResult.Removed();
                }

                switch (result.Outcome)
                {
                    case QueueItemOutcome.Completed:
                        SetQueueItemVisualState(finishedItem, DownloadQueueState.Completed, "完成", "已完成", showProgress: true, progressValue: 100);
                        _downloadQueue.Remove(finishedItem);
                        _queueRunCompletedCount++;
                        ScheduleDownloadQueueSave();
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
                        ScheduleDownloadQueueSave();
                        StatusText.Text = $"下载失败，已跳过: {finishedItem.Title}";
                        LogInfo("queue", $"[{operationId}] 队列项失败并已跳过: videoId={finishedItem.VideoId}, failed={_queueRunFailedCount}");
                        break;
                }

                UpdateQueueRuntimeSummaryUi();
            }

            if (_isPauseRequested || _hasPausedQueue)
            {
                _hasPausedQueue = true;
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
            _activeDownloadTargets.Clear();
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
        var previousTargetPath = item.TargetPath;
        _currentQueueDownloadItem = item;
        item.IsDownloading = true;
        item.HasError = false;
        SetQueueItemVisualState(item, DownloadQueueState.Resolving, "解析", "读取完整标题和视频源", showProgress: true, isProgressIndeterminate: true);
        UpdateQueueRuntimeSummaryUi();
        LogInfo("queue", $"[{operationId}] 开始处理队列项: videoId={item.VideoId}, title={item.Title}, requestedQuality={item.Quality}");

        CancellationToken queueCancellationToken = default;
        try
        {
            queueCancellationToken = CreateQueueItemCancellationToken(item);
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
                    var verified = await EnsureVerifiedSessionAsync("下载前遇到 Cloudflare 验证，正在打开验证窗口...", queueCancellationToken, ex);
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
                queueCancellationToken.ThrowIfCancellationRequested();
                if (!_downloadQueue.Contains(item)) return QueueItemProcessResult.Removed();

                SetQueueItemVisualState(item, DownloadQueueState.Downloading, "下载", "下载中", showProgress: true, progressValue: 0);
                UpdateQueueRuntimeSummaryUi();
                LogInfo("queue", $"[{operationId}] 开始下载队列项: videoId={item.VideoId}, quality={item.Quality}, type={item.Type}");
                // 路径变化必须先落盘，避免应用意外退出后找不到这个断点；普通状态可合并。
                if (!string.Equals(previousTargetPath, item.TargetPath, StringComparison.OrdinalIgnoreCase))
                    await TrySaveDownloadQueueAsync("queue", "保存下载目标路径失败");
                else
                    ScheduleDownloadQueueSave();
                var downloaded = await DownloadSourceAsync(source, item.Title, queueCancellationToken, item);
                if (!_downloadQueue.Contains(item))
                {
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
        catch (OperationCanceledException) when (queueCancellationToken.IsCancellationRequested)
        {
            SetQueueItemVisualState(item, DownloadQueueState.Paused, "暂停", "已暂停", showProgress: true, progressValue: item.ProgressValue);
            return QueueItemProcessResult.Paused();
        }
        catch (Exception ex)
        {
            // 包括下载失败处理内部的重解析/重试异常，不能传播到队列调度器。
            LogError("queue", $"[{operationId}] 队列项处理失败: videoId={item.VideoId}", ex);
            SetQueueItemVisualState(item, DownloadQueueState.Error, "失败", "下载失败");
            return QueueItemProcessResult.Error();
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

    private void DeleteQueueItemTemporaryFile(DownloadQueueItem item, string? activeTarget = null)
    {
        var path = activeTarget ?? item.TargetPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var directory = EnsureDownloadDirectory();
            var targetPath = DownloadPathGuard.EnsureWithinDirectory(directory, path);
            // 旧版队列可能有共用的路径，移除一项不应删除其他项的断点。
            if (IsDownloadPathReferenced(targetPath, item)) return;
            var temporaryPath = targetPath + ".tmp";
            File.Delete(DownloadService.GetResumeMetadataPath(temporaryPath));
            File.Delete(temporaryPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
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
                if (_activeQueueItemCancellationTokenSources.ContainsKey(item))
                {
                    CancelQueueItem(item);
                }
                else
                {
                    DeleteQueueItemTemporaryFile(item);
                }
                _downloadQueue.Remove(item);
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
        if (_isDownloadingQueue) return;
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
        if (targetIndexes.Count != indexes.Count || targetIndexes.SequenceEqual(indexes) ||
            targetIndexes.Any(index => index < 0 || index >= _downloadQueue.Count))
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
        DownloadQueueButton.Content = _isPauseRequested ? "正在暂停…" : (_isDownloadingQueue ? "暂停下载" : (_hasPausedQueue ? "继续下载" : "开始下载"));
        DownloadQueueButton.Style = (Style)FindResource(_isDownloadingQueue ? "SecondaryActionButtonStyle" : "PrimaryActionButtonStyle");
        DownloadQueueButton.IsEnabled = !_isPauseRequested && (_isDownloadingQueue || _downloadQueue.Count > 0);
        RetryFailedQueueItemsButton.IsEnabled = CanRetryFailedQueueItems && _downloadQueue.Any(item => item.QueueState == DownloadQueueState.Error);
        ClearQueueButton.IsEnabled = !_isDownloadingQueue;
        UpdateQueueRuntimeSummaryUi();
    }
}
