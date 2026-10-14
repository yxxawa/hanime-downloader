using Hanime1Downloader.CSharp.Models;
using System.IO;
using System.Net.Http;

namespace Hanime1Downloader.CSharp.Services;

public sealed partial class DownloadService
{
    // ResponseHeadersRead 的 HttpClient.Timeout 不覆盖正文；每次收到字节后重新计时。
    private async Task<int> ReadWithIdleTimeoutAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCancellation.CancelAfter(_readIdleTimeout);
        Task<int>? readTask = null;
        try
        {
            readTask = input.ReadAsync(buffer, readCancellation.Token).AsTask();
            // WaitAsync 同时覆盖没有正确响应取消的流，避免无限等待。
            return await readTask.WaitAsync(readCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            input.Dispose();
            if (readTask is not null)
            {
                _ = readTask.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"连续 {_readIdleTimeout.TotalSeconds:0} 秒未收到下载数据，连接已中断，将按重试设置恢复。", ex);
        }
    }

    private async Task<byte[]> ReadBufferedResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken, Action<int>? onBytesRead = null)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await ReadWithIdleTimeoutAsync(input, buffer, cancellationToken)) > 0)
        {
            output.Write(buffer, 0, count);
            onBytesRead?.Invoke(count);
        }
        if (response.Content.Headers.ContentLength is long expected && output.Length != expected)
        {
            throw new IOException($"下载流提前结束：收到 {output.Length} 字节，预期 {expected} 字节。");
        }
        return output.ToArray();
    }

    // 必须重试整个正文读取，仅重试请求头无法恢复卡住的播放列表/分片/密钥。
    private async Task<byte[]> ReadResponseWithRetryAsync(Func<HttpRequestMessage> requestFactory,
        Action<HttpResponseMessage> validateResponse, CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async (_, token) =>
        {
            using var request = requestFactory();
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            validateResponse(response);
            return await ReadBufferedResponseAsync(response, token);
        }, cancellationToken);
    }

    // 独立于网络读流刷新状态；断流时不能继续显示上一次的非零速度和 ETA。
    private sealed class DownloadProgressMonitor : IProgress<DownloadProgressInfo>, IAsyncDisposable
    {
        private readonly IProgress<DownloadProgressInfo>? _target;
        private readonly CancellationTokenSource _stop;
        private readonly Task _heartbeat;
        private readonly object _sync = new();
        private DownloadProgressInfo _latest = new();
        private long _lastAdvance = Environment.TickCount64;
        private bool _disposed;

        public DownloadProgressMonitor(IProgress<DownloadProgressInfo>? target, CancellationToken cancellationToken)
        {
            _target = target;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _heartbeat = target is null ? Task.CompletedTask : RunHeartbeatAsync();
        }

        public void Report(DownloadProgressInfo value)
        {
            lock (_sync)
            {
                if (_disposed || _stop.IsCancellationRequested) return;
                if (value.BytesReceived != _latest.BytesReceived)
                {
                    _lastAdvance = Environment.TickCount64;
                }
                _latest = value;
                _target?.Report(value);
            }
        }

        public void ReportRetry(int attempt, int maxAttempts)
        {
            lock (_sync)
            {
                if (_disposed || _stop.IsCancellationRequested) return;
                _latest = new DownloadProgressInfo
                {
                    BytesReceived = _latest.BytesReceived,
                    TotalBytes = _latest.TotalBytes,
                    IsWaitingForData = true,
                    StatusText = $"网络中断，正在重试（{attempt}/{maxAttempts}）"
                };
                _target?.Report(_latest);
            }
        }

        private async Task RunHeartbeatAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                {
                    lock (_sync)
                    {
                        if (_disposed || _stop.IsCancellationRequested) return;
                        if (Environment.TickCount64 - _lastAdvance < 2000) continue;
                        _latest = new DownloadProgressInfo
                        {
                            BytesReceived = _latest.BytesReceived,
                            TotalBytes = _latest.TotalBytes,
                            IsWaitingForData = true,
                            StatusText = _latest.StatusText ?? "等待数据"
                        };
                        _target?.Report(_latest);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_sync) { _disposed = true; }
            _stop.Cancel();
            await _heartbeat.ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
