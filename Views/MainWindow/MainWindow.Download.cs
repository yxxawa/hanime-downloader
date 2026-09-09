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

    private DownloadRetryPolicy CreateDownloadRetryPolicy()
    {
        // MaxAttempts 含首次尝试；0 = 不重试 → 最少 1 次尝试。
        return new DownloadRetryPolicy(maxAttempts: Math.Clamp(_settings.MaxRetries + 1, 1, 9));
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

    private async Task<bool> DownloadSourceAsync(VideoSource source, string? title, CancellationToken cancellationToken = default, DownloadQueueItem? queueItem = null, bool isRetry = false)
    {
        if (_downloadService is null)
        {
            InitSessionWithoutCf();
        }

        // 路径准备不再逃逸异常：失败只影响当前项，不会中止整个队列运行。
        string downloadDirectory;
        string targetPath;
        var videoId = queueItem?.VideoId;
        if (string.IsNullOrWhiteSpace(videoId))
        {
            videoId = _currentDetailsVideoId;
        }

        try
        {
            downloadDirectory = EnsureDownloadDirectory();
            targetPath = CreateQueueTargetPath(title, source.Type, videoId, source.Quality);
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
        }
        catch (Exception ex)
        {
            LogError("download", "准备下载路径失败", ex);
            StatusText.Text = $"下载失败: {ex.Message}";
            if (queueItem is not null)
            {
                SetQueueItemVisualState(queueItem, DownloadQueueState.Error, "失败", "下载失败");
                _queueRunCurrentProgress = 0;
                UpdateQueueRuntimeSummaryUi();
            }
            return false;
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

            // 探测链接（校验可达性 + 获取文件大小）；失败不阻塞下载。
            long? requiredBytes = null;
            M3u8Playlist? preloadedPlaylist = null;
            try
            {
                var probe = await downloadService.ProbeAsync(source.Url, cancellationToken);
                requiredBytes = probe.ContentLength;
                preloadedPlaylist = probe.Playlist;
                LogInfo("download", $"链接探测: type={probe.ContentType}, length={probe.ContentLength}, partial={probe.IsPartial}");
            }
            catch (Exception probeEx)
            {
                LogInfo("download", $"链接探测失败，继续尝试下载: {probeEx.Message}");
            }

            // 磁盘空间预检：可用空间不足时直接失败，不留下临时文件。
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(targetPath)!);
                if (drive.IsReady && requiredBytes is > 0 &&
                    drive.AvailableFreeSpace < requiredBytes.Value + 64L * 1024 * 1024)
                {
                    if (queueItem is not null)
                    {
                        SetQueueItemVisualState(queueItem, DownloadQueueState.Error, "失败", "磁盘空间不足");
                        _queueRunCurrentProgress = 0;
                        UpdateQueueRuntimeSummaryUi();
                    }
                    StatusText.Text = $"下载失败: 磁盘空间不足（可用 {FormatBytes(drive.AvailableFreeSpace)}，需要约 {FormatBytes(requiredBytes.Value)}）。";
                    LogError("download", $"磁盘空间不足: {targetPath}");
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogInfo("download", $"磁盘空间预检跳过: {ex.Message}");
            }

            if (queueItem is not null)
            {
                SetQueueItemVisualState(queueItem, DownloadQueueState.Downloading, "下载", "准备下载", showProgress: true, progressValue: 0);
                _queueRunCurrentProgress = 0;
                UpdateQueueRuntimeSummaryUi();
            }

            StatusText.Text = $"正在下载到: {targetPath}";
            var lastQueueProgressText = string.Empty;
            var progress = new Progress<DownloadProgressInfo>(info =>
            {
                var displaySpeed = info.InstantBytesPerSecond > 0 ? info.InstantBytesPerSecond : info.BytesPerSecond;
                var speedText = displaySpeed > 0 ? $"{FormatBytes((long)displaySpeed)}/s" : string.Empty;
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
                    UpdateQueueRuntimeSummaryUiThrottled();
                }

            });

            await downloadService.DownloadAsync(source.Url, targetPath, progress, source.Type, cancellationToken, preloadedPlaylist, source.Quality);
            if (queueItem is not null)
            {
                SetQueueItemVisualState(queueItem, DownloadQueueState.Finalizing, "收尾", "写入历史", showProgress: true, progressValue: 100);
                _queueRunCurrentProgress = 100;
                UpdateQueueRuntimeSummaryUi();
            }
            var historyUrl = !string.IsNullOrWhiteSpace(queueItem?.VideoId)
                ? $"https://{_settings.SiteHost}/watch?v={queueItem.VideoId}"
                : source.Url;
            if (!string.IsNullOrWhiteSpace(queueItem?.VideoId))
            {
                _historyVideoIds.Add(queueItem.VideoId);
            }
            _historyItems.Insert(0, DownloadHistoryItem.Create(DateTime.Now, Path.GetFileName(targetPath), historyUrl, targetPath));
            RefreshDownloadedFlags();
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
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient 超时等非用户取消的中断：真正的失败，而非"暂停"。
            if (queueItem is not null)
            {
                SetQueueItemVisualState(queueItem, DownloadQueueState.Error, "失败", "下载失败");
                _queueRunCurrentProgress = 0;
                UpdateQueueRuntimeSummaryUi();
            }
            StatusText.Text = "下载失败: 下载超时或连接中断。";
            LogError("download", $"下载超时/中断: {targetPath}", ex);
            return false;
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
        catch (Exception ex) when (IsCloudflareSessionError(ex) && IsSiteHostUrl(source.Url))
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
        catch (Exception ex) when (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Gone } && !IsSiteHostUrl(source.Url))
        {
            // 媒体 CDN（vdownload.hembed.com 等）的 403/410 通常是签名过期，与 Cloudflare 会话无关：
            // 不触发重验，直接强制刷新视频源（绕过 HTML 缓存拿新 token）后按重试策略重试。
            if (!isRetry)
            {
                StatusText.Text = "媒体链接已过期，正在重新解析视频源...";
                LogInfo("download", $"媒体链接过期（{(ex as HttpRequestException)?.StatusCode}），重新解析源: {source.Url}");
                try
                {
                    return await CreateDownloadRetryPolicy().ExecuteAsync(async (_, token) =>
                    {
                        VideoSource? refreshed;
                        if (queueItem is not null)
                        {
                            refreshed = await ResolveQueueItemSourceAsync(queueItem, token, forceRefresh: true);
                        }
                        else if (!string.IsNullOrWhiteSpace(videoId))
                        {
                            var details = await GetOrLoadVideoDetailsAsync(videoId, VideoDetailsLoadOptions.Basic | VideoDetailsLoadOptions.Sources, token, forceRefresh: true);
                            refreshed = details?.Sources.OrderByDescending(item => item.Quality).FirstOrDefault();
                        }
                        else
                        {
                            refreshed = null;
                        }

                        if (refreshed is null)
                        {
                            // StatusCode 为空 → 触发策略重试。
                            throw new HttpRequestException("重新解析视频源失败，未拿到可用链接。", null, null);
                        }

                        var downloaded = await DownloadSourceAsync(refreshed, queueItem?.Title ?? title, token, queueItem, isRetry: true);
                        if (!downloaded && !token.IsCancellationRequested)
                        {
                            throw new IOException("重新解析后的下载仍然失败。");
                        }

                        return downloaded;
                    }, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (queueItem is not null)
                    {
                        SetQueueItemVisualState(queueItem, DownloadQueueState.Paused, "暂停", "已暂停", showProgress: true, progressValue: queueItem.ProgressValue);
                        _queueRunCurrentProgress = queueItem.ProgressValue ?? _queueRunCurrentProgress;
                        UpdateQueueRuntimeSummaryUi();
                    }
                    StatusText.Text = "下载已暂停。";
                    return false;
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

    private string CreateQueueTargetPath(string? title, string? type, string? videoId, int quality)
    {
        // HLS 会被合并为单个媒体文件，输出统一用 .mp4（与旧版行为一致）。
        var extension = ".mp4";
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
            ? AppPaths.DefaultDownloadDirectory
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
}
