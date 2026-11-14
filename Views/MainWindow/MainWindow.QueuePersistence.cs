using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Hanime1Downloader.CSharp.Services;

namespace Hanime1Downloader.CSharp;

public partial class MainWindow
{
    // Coalesce automatic changes within a short window; explicit user edits still save immediately.
    private readonly DispatcherTimer _downloadQueueSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _downloadQueueSavePending;
    private bool _isWindowClosing;
    private Task _downloadQueueWriteTask = Task.CompletedTask;

    private void ScheduleDownloadQueueSave()
    {
        if (_isWindowClosing || !_settings.PersistDownloadQueue) return;
        _downloadQueueSavePending = true;
        if (!_downloadQueueSaveTimer.IsEnabled) _downloadQueueSaveTimer.Start();
    }

    private async void DownloadQueueSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _downloadQueueSaveTimer.Stop();
        if (_isWindowClosing || !_downloadQueueSavePending) return;
        // 慢磁盘只累积一个待写标记，不为每个定时周期堆积快照。
        if (!_downloadQueueWriteTask.IsCompleted)
        {
            _downloadQueueSaveTimer.Start();
            return;
        }
        await TrySaveDownloadQueueAsync("queue", "后台保存下载队列失败");
    }

    private Task SaveDownloadQueueAsync()
    {
        if (_isWindowClosing) return Task.CompletedTask;
        _downloadQueueSaveTimer.Stop();
        _downloadQueueSavePending = false;
        return EnqueueDownloadQueueSnapshot();
    }

    private Task EnqueueDownloadQueueSnapshot()
    {
        var previousWrite = _downloadQueueWriteTask;
        var path = DownloadQueueFilePath;
        // WPF collections stay on the dispatcher; only detached records go to the disk writer.
        var snapshot = _settings.PersistDownloadQueue ? _downloadQueue.Select(item => new DownloadQueueRecord
        {
            Title = item.Title,
            Url = item.Url,
            Type = item.Type,
            Quality = item.Quality,
            VideoId = item.VideoId,
            TargetPath = item.TargetPath,
            NamingRule = item.NamingRule,
            NamingAuthor = item.NamingAuthor,
            NamingDate = item.NamingDate,
            UploadDate = item.UploadDate,
            NamingTime = item.NamingTime,
            HasError = item.HasError
        }).ToArray() : null;

        // Serialize writes and deletes in capture order. No continuation in this chain needs the UI.
        return _downloadQueueWriteTask = Task.Run(async () =>
        {
            try { await previousWrite.ConfigureAwait(false); }
            catch { /* The previous caller already owns reporting its failure. */ }

            if (snapshot is null)
                File.Delete(path);
            else
                await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, FavoritesJsonOptions)).ConfigureAwait(false);
        });
    }

    private async Task FlushScheduledDownloadQueueSaveAsync()
    {
        while (!_isWindowClosing)
        {
            var write = _downloadQueueSavePending ? SaveDownloadQueueAsync() : _downloadQueueWriteTask;
            try { await write; }
            catch
            {
                // Retry once at the pause/completion boundary, without spinning on a persistent I/O error.
                await TrySaveDownloadQueueAsync("queue", "保存最终队列状态失败");
                return;
            }
            if (!_downloadQueueSavePending && ReferenceEquals(write, _downloadQueueWriteTask)) return;
        }
    }

    private void FlushDownloadQueueOnClosing()
    {
        _downloadQueueSaveTimer.Stop();
        _downloadQueueSavePending = false;
        try
        {
            // _isWindowClosing is already set, so no deferred write can follow this final snapshot.
            EnqueueDownloadQueueSnapshot().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogError("queue", "退出时保存下载队列失败", ex);
        }
    }
}
