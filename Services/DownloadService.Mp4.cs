using Hanime1Downloader.CSharp.Models;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Hanime1Downloader.CSharp.Services;

public sealed partial class DownloadService
{

    private async Task DownloadSingleAttemptAsync(
        Uri uri,
        string finalPath,
        string tmpPath,
        IProgress<DownloadProgressInfo>? progress,
        int attempt,
        CancellationToken cancellationToken)
    {
        var existingBytes = File.Exists(tmpPath) ? new FileInfo(tmpPath).Length : 0L;
        var requestedResume = existingBytes > 0;
        var speedWindow = new SpeedWindow();
        var startedAt = DateTime.UtcNow;

        AppLogger.Info("download", attempt > 1
            ? $"[attempt {attempt}] 下载: {uri} resume={requestedResume} existing={existingBytes}"
            : $"下载: {uri} resume={requestedResume} existing={existingBytes}");

        using var request = CreateRequest(HttpMethod.Get, uri, requestedResume ? existingBytes : null, null);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var isResume = false;
        var fileMode = FileMode.Create;
        var bytesReceived = 0L;

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var advertisedLength = response.Content.Headers.ContentRange?.Length;
            if (requestedResume && advertisedLength == existingBytes)
            {
                // 本地临时文件已完整：直接收尾。
                ReportProgress(progress, speedWindow, existingBytes, existingBytes, startedAt);
                RejectReparsePoint(finalPath);
                File.Move(tmpPath, finalPath, overwrite: true);
                return;
            }

            if (requestedResume && advertisedLength is long length && existingBytes > length)
            {
                // 服务器文件已变化：删除临时文件从头下载。
                AppLogger.Info("download", $"服务器拒绝续传(416)，删除临时文件从头下载: {tmpPath}");
                TryDelete(tmpPath);
                existingBytes = 0;
                requestedResume = false;
                response.Dispose();
                using var restartRequest = CreateRequest(HttpMethod.Get, uri, null, null);
                var restartResponse = await _httpClient.SendAsync(restartRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                restartResponse.EnsureSuccessStatusCode();
                EnsureMediaResponse(restartResponse);
                using var restarted = restartResponse;
                await ReadMediaStreamAsync(restarted, tmpPath, fileMode, bytesReceived, speedWindow, startedAt, progress, cancellationToken);
                RejectReparsePoint(finalPath);
                File.Move(tmpPath, finalPath, overwrite: true);
                return;
            }

            response.EnsureSuccessStatusCode();
        }

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            ValidatePartialResponse(response, requestedResume ? existingBytes : 0);
            isResume = requestedResume;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            EnsureMediaResponse(response);
            if (requestedResume)
            {
                AppLogger.Info("download", $"服务器未接受 Range，重新下载: {uri}");
            }
            isResume = false;
        }

        EnsureMediaResponse(response);

        long? totalBytes;
        if (response.Content.Headers.ContentRange?.Length is long rangeLength)
        {
            totalBytes = rangeLength;
            if (isResume && rangeLength < existingBytes)
            {
                throw new InvalidDataException("服务器返回的总长度小于本地临时文件，已拒绝续传。");
            }
        }
        else if (response.Content.Headers.ContentLength is long length)
        {
            totalBytes = isResume ? checked(existingBytes + length) : length;
        }
        else
        {
            totalBytes = null;
        }

        if (isResume)
        {
            fileMode = FileMode.Append;
            bytesReceived = existingBytes;
        }

        await ReadMediaStreamAsync(response, tmpPath, fileMode, bytesReceived, speedWindow, startedAt, progress, cancellationToken, totalBytes);
        RejectReparsePoint(finalPath);
        File.Move(tmpPath, finalPath, overwrite: true);
    }

    /// <summary>读取响应流写入临时文件，附带进度报告（瞬时速度）与长度校验。</summary>
    private async Task ReadMediaStreamAsync(
        HttpResponseMessage response,
        string tmpPath,
        FileMode fileMode,
        long bytesReceived,
        SpeedWindow speedWindow,
        DateTime startedAt,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken,
        long? totalBytes = null)
    {
        var keepPartialFile = bytesReceived > 0;
        var lastReportedBytes = bytesReceived;
        var lastReportedAt = Environment.TickCount64;
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                tmpPath,
                fileMode,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[BufferSize];
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                bytesReceived += bytesRead;
                keepPartialFile = bytesReceived > 0;
                speedWindow.Add(bytesRead);

                var now = Environment.TickCount64;
                var shouldReport = bytesReceived == totalBytes ||
                                   bytesReceived - lastReportedBytes >= 512 * 1024 ||
                                   now - lastReportedAt >= 150;
                if (shouldReport)
                {
                    ReportProgress(progress, speedWindow, bytesReceived, totalBytes, startedAt);
                    lastReportedBytes = bytesReceived;
                    lastReportedAt = now;
                }
            }

            await output.FlushAsync(cancellationToken);
            if (totalBytes is long expectedLength && bytesReceived != expectedLength)
            {
                throw new InvalidDataException($"下载长度校验失败：收到 {bytesReceived} 字节，预期 {expectedLength} 字节。");
            }

            ReportProgress(progress, speedWindow, bytesReceived, totalBytes, startedAt);
        }
        catch
        {
            // 从未收到任何字节且不是续传 → 删除空临时文件；否则保留以便下次续传。
            if (!keepPartialFile && File.Exists(tmpPath) && new FileInfo(tmpPath).Length == 0)
            {
                TryDelete(tmpPath);
            }
            throw;
        }
    }
}
